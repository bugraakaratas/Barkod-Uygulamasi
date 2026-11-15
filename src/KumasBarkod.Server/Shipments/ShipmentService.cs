using System.Globalization;
using System.Text.Json;
using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Server.Auth;
using KumasBarkod.Server.Data;
using KumasBarkod.Server.Ingest;
using KumasBarkod.Server.Lots;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Server.Shipments;

/// <summary>
/// Sevkiyat: aç → okut (topu ayırır) → [çıkar] → onayla (sevk edildi) ya da iptal (toplar stoğa döner).
/// <para>Her işlem TEK veritabanı işleminde (BEGIN IMMEDIATE: yazmalar sırayla). Asıl kilit veritabanında:
/// bir top aynı anda yalnızca bir aktif kalemde (<c>ux_items_one_active_per_roll</c>) ve durum geçişleri tetikleyiciyle
/// denetlenir. İki cihaz aynı topu aynı anda okutursa tam olarak biri kabul edilir.</para>
/// <para>Okutma tekrar gönderime dayanıklıdır: aynı <c>clientScanId</c> ikinci kez işlenmez, kayıtlı yanıt aynen döner
/// (ağ kopup istemci tekrar denediğinde çift kayıt olmaz). Reddedilen her okutma da sebebiyle kaydedilir.</para>
/// <para>Onay sürüm kontrollüdür (<c>expectedVersion</c>): başka cihaz liste değiştirdiyse onay reddedilir.
/// Onay ve iptal <c>requestId</c> ile tekrar gönderime dayanıklıdır.</para>
/// </summary>
public sealed class ShipmentService(SqliteConnectionFactory db, TimeProvider clock)
{
    public const int MaxLotsPerShipment = 10;

    // ---------- Aç / listele / ayrıntı ----------

    public ShipmentDetail Open(ShipmentOpenRequest? request, CurrentUser user, string? device)
    {
        var lotIds = request?.LotIds?.Distinct().ToList() ?? [];
        if (lotIds.Count is 0 or > MaxLotsPerShipment)
            throw new ApiException(400, ShipmentErrorCodes.InvalidLots, $"Sevkiyat için 1–{MaxLotsPerShipment} lot seçin.");

        var now = clock.GetUtcNow();
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        foreach (var lotId in lotIds)
        {
            var active = c.ExecuteScalar<long?>("SELECT active FROM lots WHERE lot_id = @lotId", new { lotId }, tx);
            if (active is null) throw new ApiException(400, ShipmentErrorCodes.InvalidLots, $"Lot bulunamadı (no {lotId}).");
            if (active == 0) throw new ApiException(400, ShipmentErrorCodes.InvalidLots, "Pasif lot için sevkiyat açılamaz.");
        }

        var year = now.ToLocalTime().Year;
        var seq = c.ExecuteScalar<long>("""
            INSERT INTO shipment_counter (year, last) VALUES (@year, 1)
            ON CONFLICT (year) DO UPDATE SET last = last + 1
            RETURNING last
            """, new { year }, tx);
        var no = $"SV-{year}-{seq.ToString("D5", CultureInfo.InvariantCulture)}";
        var ts = Timestamps.Format(now);
        var id = c.ExecuteScalar<long>("""
            INSERT INTO shipments (shipment_no, status, opened_by, opened_at, opened_device, last_activity_at)
            VALUES (@no, 'open', @user, @ts, @device, @ts) RETURNING shipment_id
            """, new { no, user = user.UserId, ts, device }, tx);
        foreach (var lotId in lotIds)
            c.Execute("INSERT INTO shipment_lots (shipment_id, lot_id) VALUES (@id, @lotId)", new { id, lotId }, tx);
        DataVersion.Bump(c, tx);
        tx.Commit();
        return Get(id);
    }

    /// <param name="status">open = açık olanlar; diğer = son kapananlar (en fazla <paramref name="limit"/>).</param>
    public IReadOnlyList<ShipmentSummary> List(string? status, int limit = 50)
    {
        var open = status is null or ShipmentStatuses.Open;
        using var c = db.Open();
        return c.Query<SummaryRow>($"""
            SELECT s.shipment_id AS ShipmentId, s.shipment_no AS ShipmentNo, s.status AS Status,
                   (SELECT group_concat(l.name, ', ') FROM shipment_lots sl JOIN lots l ON l.lot_id = sl.lot_id
                     WHERE sl.shipment_id = s.shipment_id) AS LotNames,
                   (SELECT count(*) FROM shipment_items i WHERE i.shipment_id = s.shipment_id AND i.status = 'active') AS Count,
                   (SELECT coalesce(sum(r.length_cm), 0) FROM shipment_items i JOIN rolls r ON r.cut_id = i.cut_id
                     WHERE i.shipment_id = s.shipment_id AND i.status = 'active') AS LengthCm,
                   uo.name AS OpenedBy, s.opened_at AS OpenedAt, s.last_activity_at AS LastActivityAt,
                   s.closed_at AS ClosedAt, uc.name AS ClosedBy
            FROM shipments s JOIN users uo ON uo.user_id = s.opened_by LEFT JOIN users uc ON uc.user_id = s.closed_by
            WHERE {(open ? "s.status = 'open'" : "s.status <> 'open'")}
            ORDER BY {(open ? "s.opened_at, s.shipment_id" : "s.closed_at DESC, s.shipment_id DESC")}
            LIMIT @limit
            """, new { limit = Math.Clamp(limit, 1, 500) })
            .Select(r => new ShipmentSummary(r.ShipmentId, r.ShipmentNo, r.Status, r.LotNames ?? "", r.Count, r.LengthCm,
                r.OpenedBy, r.OpenedAt, r.LastActivityAt, r.ClosedAt, r.ClosedBy))
            .ToList();
    }

    public ShipmentDetail Get(long shipmentId)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: true);
        var d = Detail(c, tx, shipmentId);
        tx.Commit();
        return d;
    }

    // ---------- Okut / çıkar ----------

    public ScanResponse Scan(long shipmentId, ScanRequest? request, CurrentUser user, string? device) =>
        Process(shipmentId, request, user, device, remove: false);

    /// <summary>Çıkarma modu: okutulan top sevkiyattan çıkar, stoğa döner (tırdan indirilen top).</summary>
    public ScanResponse Remove(long shipmentId, ScanRequest? request, CurrentUser user, string? device) =>
        Process(shipmentId, request, user, device, remove: true);

    private ScanResponse Process(long shipmentId, ScanRequest? request, CurrentUser user, string? device, bool remove)
    {
        var clientScanId = request?.ClientScanId?.Trim() ?? "";
        var raw = request?.RawInput ?? "";
        if (clientScanId.Length is 0 or > 64)
            throw new ApiException(400, ServerErrorCodes.InvalidRequest, "Okutma kimliği eksik.");
        if (raw.Length > 100) raw = raw[..100];

        var now = Timestamps.Format(clock.GetUtcNow());
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);

        // Aynı okutma tekrar geldi (yanıt kaybolmuştu): ilk yanıt aynen döner, hiçbir şey değişmez.
        var previous = c.QuerySingleOrDefault<string?>(
            "SELECT response_json FROM scan_events WHERE client_scan_id = @clientScanId", new { clientScanId }, tx);
        if (previous is not null)
        {
            tx.Commit();
            return JsonSerializer.Deserialize<ScanResponse>(previous, ServerApiContract.Json)!;
        }

        var ship = c.QuerySingleOrDefault<ShipRow>(
            "SELECT shipment_no AS No, status AS Status, version AS Version FROM shipments WHERE shipment_id = @shipmentId",
            new { shipmentId }, tx) ?? throw NotFound(shipmentId);

        var ctx = new ScanContext(c, tx, shipmentId, clientScanId, raw, user, device, now, remove ? "remove" : "add");

        if (ship.Status != ShipmentStatuses.Open)
            return Reject(ctx, null, ScanCodes.ShipmentNotOpen, ScanSeverity.Error,
                $"{ship.No} {ShipmentStatuses.Display(ship.Status).ToLowerInvariant()}; okutma yapılamaz.");

        if (!CutIds.TryParseScan(raw, out var cutId))
            return Reject(ctx, null, ScanCodes.InvalidBarcode, ScanSeverity.Error,
                $"Okunan değer bir top barkodu değil: \"{raw.Trim()}\".");

        var roll = c.QuerySingleOrDefault<RollRow>("""
            SELECT r.cut_id AS CutId, r.state AS State, r.qc_status AS QcStatus, r.lot_id AS LotId, l.name AS LotName,
                   r.lot_code_raw AS LotRaw, r.length_cm AS LengthCm
            FROM rolls r LEFT JOIN lots l ON l.lot_id = r.lot_id WHERE r.cut_id = @cutId
            """, new { cutId }, tx);
        if (roll is null)
            return Reject(ctx, cutId, ScanCodes.NotFound, ScanSeverity.Error,
                $"{cutId} sistemde yok. Kalite kontrol kaydı depoya henüz ulaşmamış olabilir: topu yüklemeyin, ayırın ve yetkiliye haber verin.");

        var activeItem = c.QuerySingleOrDefault<ActiveItemRow>("""
            SELECT i.item_id AS ItemId, i.shipment_id AS ShipmentId, s.shipment_no AS No, s.status AS Status,
                   s.closed_at AS ClosedAt, u.name AS AddedBy
            FROM shipment_items i JOIN shipments s ON s.shipment_id = i.shipment_id JOIN users u ON u.user_id = i.added_by
            WHERE i.cut_id = @cutId AND i.status = 'active'
            """, new { cutId }, tx);

        return remove ? RemoveRoll(ctx, roll, activeItem) : AddRoll(ctx, roll, activeItem);
    }

    private ScanResponse AddRoll(ScanContext x, RollRow roll, ActiveItemRow? activeItem)
    {
        var id = roll.CutId;
        if (roll.State == RollStates.Quarantined)
            return Reject(x, id, ScanCodes.Quarantined, ScanSeverity.Error,
                $"{id} karantinada: aynı barkod iki topta var. Topu yüklemeyin, ayırın ve yetkiliye bildirin.");
        if (activeItem is not null && activeItem.ShipmentId == x.ShipmentId)
            return Reject(x, id, ScanCodes.AlreadyInThisShipment, ScanSeverity.Warning,
                $"{id} bu sevkiyata zaten okutuldu ({activeItem.AddedBy}). Tekrar eklenmedi.");
        if (roll.State == RollStates.Shipped || activeItem?.Status == ShipmentStatuses.Confirmed)
            return Reject(x, id, ScanCodes.AlreadyShipped, ScanSeverity.Error,
                $"{id} daha önce sevk edildi" + (activeItem is null ? "" : $": {activeItem.No}, {Local(activeItem.ClosedAt)}") +
                ". Bu tıra yüklemeyin.");
        if (roll.State == RollStates.Voided || roll.QcStatus == "voided")
            return Reject(x, id, ScanCodes.Voided, ScanSeverity.Error,
                $"{id} kalite kontrolde iptal edilmiş; sevk edilemez. Topu yüklemeyin.");
        if (activeItem is not null)
            return Reject(x, id, ScanCodes.ReservedInOtherShipment, ScanSeverity.Error,
                $"{id} başka bir sevkiyatta: {activeItem.No} ({activeItem.AddedBy}). Bu tıra yüklemeyin.");
        if (roll.LotId is null)
            return Reject(x, id, ScanCodes.LotUnassigned, ScanSeverity.Error,
                $"{id} hiçbir lota bağlı değil (kalite kontrolde \"{roll.LotRaw}\" yazılmış). Yönetim Lotlar ekranından bağlamalı; şimdilik yüklemeyin.");

        var lots = ShipmentLots(x.C, x.Tx, x.ShipmentId);
        if (lots.All(l => l.LotId != roll.LotId))
            return Reject(x, id, ScanCodes.WrongLot, ScanSeverity.Error,
                $"Bu barkod seçili lota ait değil. Barkodun lotu: {roll.LotName}. Seçili lot: {string.Join(", ", lots.Select(l => l.Name))}.");
        if (roll.State != RollStates.InStock)
            return Reject(x, id, ScanCodes.NotAvailable, ScanSeverity.Error,
                $"{id} sevkiyata uygun değil ({RollStateNames.Display(roll.State)}).");

        // Kabul: top ayrılır. Koşullu güncelleme + benzersiz indeks: başka bir işlem araya giremez.
        if (x.C.Execute("""
                UPDATE rolls SET state = 'reserved', updated_at = @Now, version = version + 1
                WHERE cut_id = @id AND state = 'in_stock'
                """, new { x.Now, id }, x.Tx) != 1)
            return Reject(x, id, ScanCodes.NotAvailable, ScanSeverity.Error, $"{id} az önce başka bir işlemde değişti; tekrar okutun.");

        var eventId = InsertEvent(x, id, ScanCodes.Ok, accepted: true, responseJson: "");
        x.C.Execute("""
            INSERT INTO shipment_items (shipment_id, cut_id, status, added_at, added_by, scan_event_id)
            VALUES (@ShipmentId, @id, 'active', @Now, @UserId, @eventId)
            """, new { x.ShipmentId, id, x.Now, x.User.UserId, eventId }, x.Tx);
        Movement(x, id, MovementKinds.Reserved, RollStates.InStock, RollStates.Reserved, eventId, null);
        var version = Touch(x, changeItems: true);
        DataVersion.Bump(x.C, x.Tx);

        var response = new ScanResponse(true, ScanCodes.Ok, ScanSeverity.Success,
            $"{id} eklendi · {roll.LotName} · {Length.FormatGrouped(roll.LengthCm)} m",
            new ScanRoll(id, CutIds.ToBarcode(id), roll.LotName!, roll.LengthCm), Stats(x.C, x.Tx, x.ShipmentId), version);
        x.C.Execute("UPDATE scan_events SET response_json = @json WHERE scan_event_id = @eventId",
            new { json = JsonSerializer.Serialize(response, ServerApiContract.Json), eventId }, x.Tx);
        x.Tx.Commit();
        return response;
    }

    private ScanResponse RemoveRoll(ScanContext x, RollRow roll, ActiveItemRow? activeItem)
    {
        var id = roll.CutId;
        if (activeItem is null || activeItem.ShipmentId != x.ShipmentId)
            return Reject(x, id, ScanCodes.NotInThisShipment, ScanSeverity.Warning,
                $"{id} bu sevkiyatta değil; çıkarılacak bir şey yok.");

        var eventId = InsertEvent(x, id, ScanCodes.Removed, accepted: true, responseJson: "");
        EndItem(x.C, x.Tx, activeItem.ItemId, "removed", x.Now, x.User.UserId, "Okutularak çıkarıldı (tırdan indirildi)");
        if (x.C.Execute("UPDATE rolls SET state = 'in_stock', updated_at = @Now, version = version + 1 WHERE cut_id = @id AND state = 'reserved'",
                new { x.Now, id }, x.Tx) != 1)
            throw new InvalidOperationException($"{id} ayrılmış durumda değil; tutarsızlık.");
        Movement(x, id, MovementKinds.Unreserved, RollStates.Reserved, RollStates.InStock, eventId, "Sevkiyattan çıkarıldı");
        var version = Touch(x, changeItems: true);
        DataVersion.Bump(x.C, x.Tx);

        var response = new ScanResponse(true, ScanCodes.Removed, ScanSeverity.Success,
            $"{id} sevkiyattan çıkarıldı, stoğa döndü.",
            new ScanRoll(id, CutIds.ToBarcode(id), roll.LotName ?? roll.LotRaw, roll.LengthCm), Stats(x.C, x.Tx, x.ShipmentId), version);
        x.C.Execute("UPDATE scan_events SET response_json = @json WHERE scan_event_id = @eventId",
            new { json = JsonSerializer.Serialize(response, ServerApiContract.Json), eventId }, x.Tx);
        x.Tx.Commit();
        return response;
    }

    private ScanResponse Reject(ScanContext x, string? cutId, string code, string severity, string message)
    {
        // Yalnızca açık sevkiyatın "son işlem" zamanı güncellenir; kapanmış kayıt değişmez.
        x.C.Execute("UPDATE shipments SET last_activity_at = @Now WHERE shipment_id = @ShipmentId AND status = 'open'",
            new { x.Now, x.ShipmentId }, x.Tx);
        // Ret de kaydedilir: "bu barkod neden reddedildi?" sorusu her zaman yanıtlanır.
        var stats = Stats(x.C, x.Tx, x.ShipmentId, extraRejected: x.Kind == "add" ? 1 : 0);
        var response = new ScanResponse(false, code, severity, message, null, stats,
            x.C.ExecuteScalar<long>("SELECT version FROM shipments WHERE shipment_id = @ShipmentId", new { x.ShipmentId }, x.Tx));
        InsertEvent(x, cutId, code, accepted: false, responseJson: JsonSerializer.Serialize(response, ServerApiContract.Json));
        DataVersion.Bump(x.C, x.Tx);
        x.Tx.Commit();
        return response;
    }

    // ---------- Onay / iptal ----------

    public ShipmentDetail Confirm(long shipmentId, ShipmentConfirmRequest? request, CurrentUser user)
    {
        var requestId = RequireRequestId(request?.RequestId);
        var now = Timestamps.Format(clock.GetUtcNow());
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        var ship = c.QuerySingleOrDefault<ShipRow>("""
            SELECT shipment_no AS No, status AS Status, version AS Version, close_request_id AS CloseRequestId
            FROM shipments WHERE shipment_id = @shipmentId
            """, new { shipmentId }, tx) ?? throw NotFound(shipmentId);

        if (ship.Status == ShipmentStatuses.Confirmed && ship.CloseRequestId == requestId)
        {
            tx.Commit();
            return Get(shipmentId);   // aynı onay tekrar geldi: zaten onaylı
        }
        if (ship.Status != ShipmentStatuses.Open)
            throw new ApiException(409, ShipmentErrorCodes.NotOpen, $"{ship.No} {ShipmentStatuses.Display(ship.Status).ToLowerInvariant()}; onaylanamaz.");
        if (ship.Version != request!.ExpectedVersion)
            throw new ApiException(409, ShipmentErrorCodes.VersionMismatch,
                "Liste değişti (başka cihazdan okutma ya da çıkarma yapıldı). Listeyi kontrol edip tekrar onaylayın.");

        var items = c.Query<string>("SELECT cut_id FROM shipment_items WHERE shipment_id = @shipmentId AND status = 'active'",
            new { shipmentId }, tx).ToList();
        if (items.Count == 0)
            throw new ApiException(400, ShipmentErrorCodes.Empty, "Sevkiyatta top yok; boş sevkiyat onaylanamaz (iptal edin).");

        foreach (var id in items)
        {
            if (c.Execute("UPDATE rolls SET state = 'shipped', updated_at = @now, version = version + 1 WHERE cut_id = @id AND state = 'reserved'",
                    new { now, id }, tx) != 1)
                throw new InvalidOperationException($"{id} ayrılmış durumda değil; onay yapılmadı.");
            c.Execute("""
                INSERT INTO stock_movements (cut_id, kind, from_state, to_state, shipment_id, user_id, occurred_at, recorded_at, note)
                VALUES (@id, 'shipped', 'reserved', 'shipped', @shipmentId, @user, @now, @now, @note)
                """, new { id, shipmentId, user = user.UserId, now, note = $"{ship.No} onaylandı" }, tx);
        }
        c.Execute("""
            UPDATE shipments SET status = 'confirmed', closed_by = @user, closed_at = @now, close_request_id = @requestId,
                                 last_activity_at = @now, version = version + 1
            WHERE shipment_id = @shipmentId
            """, new { user = user.UserId, now, requestId, shipmentId }, tx);
        DataVersion.Bump(c, tx);
        tx.Commit();
        return Get(shipmentId);
    }

    public ShipmentDetail Cancel(long shipmentId, ShipmentCancelRequest? request, CurrentUser user)
    {
        var requestId = RequireRequestId(request?.RequestId);
        var reason = request?.Reason?.Trim() ?? "";
        if (reason.Length is 0 or > 200 || reason.Any(char.IsControl))
            throw new ApiException(400, ServerErrorCodes.InvalidRequest, "İptal sebebini seçin.");
        var now = Timestamps.Format(clock.GetUtcNow());

        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        var ship = c.QuerySingleOrDefault<ShipRow>("""
            SELECT shipment_no AS No, status AS Status, version AS Version, close_request_id AS CloseRequestId
            FROM shipments WHERE shipment_id = @shipmentId
            """, new { shipmentId }, tx) ?? throw NotFound(shipmentId);
        if (ship.Status == ShipmentStatuses.Cancelled && ship.CloseRequestId == requestId)
        {
            tx.Commit();
            return Get(shipmentId);
        }
        if (ship.Status != ShipmentStatuses.Open)
            throw new ApiException(409, ShipmentErrorCodes.NotOpen, $"{ship.No} {ShipmentStatuses.Display(ship.Status).ToLowerInvariant()}; iptal edilemez.");

        foreach (var (itemId, id) in c.Query<(long, string)>(
                     "SELECT item_id, cut_id FROM shipment_items WHERE shipment_id = @shipmentId AND status = 'active'", new { shipmentId }, tx).ToList())
        {
            EndItem(c, tx, itemId, "cancelled", now, user.UserId, $"Sevkiyat iptal: {reason}");
            if (c.Execute("UPDATE rolls SET state = 'in_stock', updated_at = @now, version = version + 1 WHERE cut_id = @id AND state = 'reserved'",
                    new { now, id }, tx) != 1)
                throw new InvalidOperationException($"{id} ayrılmış durumda değil; iptal yapılmadı.");
            c.Execute("""
                INSERT INTO stock_movements (cut_id, kind, from_state, to_state, shipment_id, user_id, occurred_at, recorded_at, note)
                VALUES (@id, 'unreserved', 'reserved', 'in_stock', @shipmentId, @user, @now, @now, @note)
                """, new { id, shipmentId, user = user.UserId, now, note = $"{ship.No} iptal edildi: {reason}" }, tx);
        }
        c.Execute("""
            UPDATE shipments SET status = 'cancelled', closed_by = @user, closed_at = @now, close_request_id = @requestId,
                                 close_reason = @reason, last_activity_at = @now, version = version + 1
            WHERE shipment_id = @shipmentId
            """, new { user = user.UserId, now, requestId, reason, shipmentId }, tx);
        DataVersion.Bump(c, tx);
        tx.Commit();
        return Get(shipmentId);
    }

    // ---------- Kesim alımından çağrılır (QC iptali / karantina ayrılmış topa gelirse) ----------

    /// <summary>Ayrılmış topun aktif kalemini kapatır (top sevkiyattan düşer). Durumu çağıran değiştirir.</summary>
    public static void DetachReservedRoll(SqliteConnection c, SqliteTransaction tx, string cutId, string now, string reason)
    {
        var found = c.Query<(long ItemId, long ShipmentId)>(
            "SELECT item_id, shipment_id FROM shipment_items WHERE cut_id = @cutId AND status = 'active'", new { cutId }, tx).ToList();
        if (found.Count == 0) return;
        var i = found[0];
        c.Execute("UPDATE shipment_items SET status = 'removed', ended_at = @now, end_reason = @reason WHERE item_id = @ItemId",
            new { now, reason, i.ItemId }, tx);
        c.Execute("UPDATE shipments SET version = version + 1, last_activity_at = @now WHERE shipment_id = @ShipmentId",
            new { now, i.ShipmentId }, tx);
    }

    // ---------- Yardımcılar ----------

    private ShipmentDetail Detail(SqliteConnection c, SqliteTransaction tx, long shipmentId)
    {
        var h = c.QuerySingleOrDefault<HeaderRow>("""
            SELECT s.shipment_id AS ShipmentId, s.shipment_no AS ShipmentNo, s.status AS Status, s.version AS Version,
                   uo.name AS OpenedBy, s.opened_at AS OpenedAt, s.last_activity_at AS LastActivityAt,
                   s.closed_at AS ClosedAt, uc.name AS ClosedBy, s.close_reason AS CloseReason
            FROM shipments s JOIN users uo ON uo.user_id = s.opened_by LEFT JOIN users uc ON uc.user_id = s.closed_by
            WHERE s.shipment_id = @shipmentId
            """, new { shipmentId }, tx) ?? throw NotFound(shipmentId);

        var lots = ShipmentLots(c, tx, shipmentId);
        var items = c.Query<ItemRow>("""
            SELECT i.cut_id AS CutId, coalesce(l.name, r.lot_code_raw) AS LotName, r.length_cm AS LengthCm,
                   i.added_at AS AddedAt, u.name AS AddedBy
            FROM shipment_items i JOIN rolls r ON r.cut_id = i.cut_id LEFT JOIN lots l ON l.lot_id = r.lot_id
            JOIN users u ON u.user_id = i.added_by
            WHERE i.shipment_id = @shipmentId AND i.status = 'active'
            ORDER BY i.item_id DESC
            """, new { shipmentId }, tx)
            .Select(r => new ShipmentItemInfo(r.CutId, CutIds.ToBarcode(r.CutId), r.LotName, r.LengthCm, r.AddedAt, r.AddedBy)).ToList();
        var scans = c.Query<ScanRow>("""
            SELECT e.scan_event_id AS ScanEventId, e.kind AS Kind, e.raw_input AS RawInput, e.cut_id AS CutId,
                   e.result_code AS Code, e.accepted AS Accepted, e.response_json AS Json, e.server_time AS ServerTime, u.name AS User
            FROM scan_events e JOIN users u ON u.user_id = e.user_id
            WHERE e.shipment_id = @shipmentId ORDER BY e.scan_event_id DESC LIMIT 30
            """, new { shipmentId }, tx)
            .Select(s => new ScanEventInfo(s.ScanEventId, s.Kind, s.RawInput, s.CutId, s.Code, s.Accepted == 1,
                s.Json.Length == 0 ? "" : JsonSerializer.Deserialize<ScanResponse>(s.Json, ServerApiContract.Json)!.Message,
                s.ServerTime, s.User)).ToList();

        var warnings = new List<string>();
        if (h.Status == ShipmentStatuses.Open)
        {
            foreach (var other in c.Query<(string No, string Lot, string User)>("""
                         SELECT DISTINCT s.shipment_no, l.name, u.name
                         FROM shipment_lots mine JOIN shipment_lots sl ON sl.lot_id = mine.lot_id AND sl.shipment_id <> mine.shipment_id
                         JOIN shipments s ON s.shipment_id = sl.shipment_id AND s.status = 'open'
                         JOIN lots l ON l.lot_id = sl.lot_id JOIN users u ON u.user_id = s.opened_by
                         WHERE mine.shipment_id = @shipmentId
                         """, new { shipmentId }, tx))
                warnings.Add($"{other.Lot} başka bir açık sevkiyatta da var: {other.No} ({other.User}). Aynı topu iki tıra yüklemeyin; sistem ikinci okutmayı reddeder.");
        }

        return new ShipmentDetail(h.ShipmentId, h.ShipmentNo, h.Status, h.Version, h.OpenedBy, h.OpenedAt, h.LastActivityAt,
            h.ClosedAt, h.ClosedBy, h.CloseReason, lots, Stats(c, tx, shipmentId), items, scans, warnings);
    }

    private static List<ShipmentLotInfo> ShipmentLots(SqliteConnection c, SqliteTransaction tx, long shipmentId) =>
        c.Query<(long LotId, string Name, long Count, long Cm)>("""
            SELECT l.lot_id, l.name,
                   (SELECT count(*) FROM rolls r WHERE r.lot_id = l.lot_id AND r.state = 'in_stock'),
                   (SELECT coalesce(sum(length_cm), 0) FROM rolls r WHERE r.lot_id = l.lot_id AND r.state = 'in_stock')
            FROM shipment_lots sl JOIN lots l ON l.lot_id = sl.lot_id WHERE sl.shipment_id = @shipmentId ORDER BY l.name
            """, new { shipmentId }, tx).Select(x => new ShipmentLotInfo(x.LotId, x.Name, x.Count, x.Cm)).ToList();

    private static ShipmentStats Stats(SqliteConnection c, SqliteTransaction tx, long shipmentId, int extraRejected = 0)
    {
        var s = c.QuerySingle<(long Count, long Cm, long Rejected, long Remaining, long RemainingCm)>("""
            SELECT (SELECT count(*) FROM shipment_items WHERE shipment_id = @shipmentId AND status = 'active'),
                   (SELECT coalesce(sum(r.length_cm), 0) FROM shipment_items i JOIN rolls r ON r.cut_id = i.cut_id
                     WHERE i.shipment_id = @shipmentId AND i.status = 'active'),
                   (SELECT count(*) FROM scan_events WHERE shipment_id = @shipmentId AND accepted = 0 AND kind = 'add'),
                   (SELECT count(*) FROM rolls r JOIN shipment_lots sl ON sl.lot_id = r.lot_id
                     WHERE sl.shipment_id = @shipmentId AND r.state = 'in_stock'),
                   (SELECT coalesce(sum(r.length_cm), 0) FROM rolls r JOIN shipment_lots sl ON sl.lot_id = r.lot_id
                     WHERE sl.shipment_id = @shipmentId AND r.state = 'in_stock')
            """, new { shipmentId }, tx);
        return new ShipmentStats(s.Count, s.Cm, s.Rejected + extraRejected, s.Remaining, s.RemainingCm);
    }

    private static long InsertEvent(ScanContext x, string? cutId, string code, bool accepted, string responseJson) =>
        x.C.ExecuteScalar<long>("""
            INSERT INTO scan_events (client_scan_id, shipment_id, kind, raw_input, cut_id, result_code, accepted, user_id, device, server_time, response_json)
            VALUES (@ClientScanId, @ShipmentId, @Kind, @Raw, @cutId, @code, @a, @UserId, @Device, @Now, @responseJson)
            RETURNING scan_event_id
            """, new { x.ClientScanId, x.ShipmentId, x.Kind, x.Raw, cutId, code, a = accepted ? 1 : 0, x.User.UserId, x.Device, x.Now, responseJson }, x.Tx);

    private static void Movement(ScanContext x, string cutId, string kind, string from, string to, long eventId, string? note) =>
        x.C.Execute("""
            INSERT INTO stock_movements (cut_id, kind, from_state, to_state, shipment_id, scan_event_id, user_id, device_id, occurred_at, recorded_at, note)
            VALUES (@cutId, @kind, @from, @to, @ShipmentId, @eventId, @UserId, @Device, @Now, @Now, @note)
            """, new { cutId, kind, from, to, x.ShipmentId, eventId, x.User.UserId, x.Device, x.Now, note }, x.Tx);

    private static void EndItem(SqliteConnection c, SqliteTransaction tx, long itemId, string status, string now, long userId, string reason) =>
        c.Execute("UPDATE shipment_items SET status = @status, ended_at = @now, ended_by = @userId, end_reason = @reason WHERE item_id = @itemId",
            new { status, now, userId, reason, itemId }, tx);

    /// <returns>Sevkiyatın yeni sürümü (kalemler değiştiyse artar; ret yalnızca son işlem zamanını günceller).</returns>
    private static long Touch(ScanContext x, bool changeItems)
    {
        x.C.Execute($"UPDATE shipments SET last_activity_at = @Now{(changeItems ? ", version = version + 1" : "")} WHERE shipment_id = @ShipmentId",
            new { x.Now, x.ShipmentId }, x.Tx);
        return x.C.ExecuteScalar<long>("SELECT version FROM shipments WHERE shipment_id = @ShipmentId", new { x.ShipmentId }, x.Tx);
    }

    private static string RequireRequestId(string? requestId) =>
        requestId is { Length: > 0 and <= 64 } ? requestId : throw new ApiException(400, ServerErrorCodes.InvalidRequest, "İstek kimliği eksik.");

    private static ApiException NotFound(long id) => new(404, ShipmentErrorCodes.NotFound, $"Sevkiyat bulunamadı (no {id}).");

    private static string Local(string? utc) =>
        utc is null ? "" : Timestamps.Parse(utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("tr-TR"));

    private sealed record ScanContext(SqliteConnection C, SqliteTransaction Tx, long ShipmentId, string ClientScanId, string Raw,
        CurrentUser User, string? Device, string Now, string Kind);

    private sealed class ShipRow { public string No { get; init; } = ""; public string Status { get; init; } = ""; public long Version { get; init; } public string? CloseRequestId { get; init; } }
    private sealed class RollRow { public string CutId { get; init; } = ""; public string State { get; init; } = ""; public string QcStatus { get; init; } = ""; public long? LotId { get; init; } public string? LotName { get; init; } public string LotRaw { get; init; } = ""; public long LengthCm { get; init; } }
    private sealed class ActiveItemRow { public long ItemId { get; init; } public long ShipmentId { get; init; } public string No { get; init; } = ""; public string Status { get; init; } = ""; public string? ClosedAt { get; init; } public string AddedBy { get; init; } = ""; }
    private sealed class SummaryRow { public long ShipmentId { get; init; } public string ShipmentNo { get; init; } = ""; public string Status { get; init; } = ""; public string? LotNames { get; init; } public long Count { get; init; } public long LengthCm { get; init; } public string OpenedBy { get; init; } = ""; public string OpenedAt { get; init; } = ""; public string LastActivityAt { get; init; } = ""; public string? ClosedAt { get; init; } public string? ClosedBy { get; init; } }
    private sealed class HeaderRow { public long ShipmentId { get; init; } public string ShipmentNo { get; init; } = ""; public string Status { get; init; } = ""; public long Version { get; init; } public string OpenedBy { get; init; } = ""; public string OpenedAt { get; init; } = ""; public string LastActivityAt { get; init; } = ""; public string? ClosedAt { get; init; } public string? ClosedBy { get; init; } public string? CloseReason { get; init; } }
    private sealed class ItemRow { public string CutId { get; init; } = ""; public string LotName { get; init; } = ""; public long LengthCm { get; init; } public string AddedAt { get; init; } = ""; public string AddedBy { get; init; } = ""; }
    private sealed class ScanRow { public long ScanEventId { get; init; } public string Kind { get; init; } = ""; public string RawInput { get; init; } = ""; public string? CutId { get; init; } public string Code { get; init; } = ""; public long Accepted { get; init; } public string Json { get; init; } = ""; public string ServerTime { get; init; } = ""; public string User { get; init; } = ""; }
}
