using System.Globalization;
using System.Text.Json;
using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;
using KumasBarkod.Server.Auth;
using KumasBarkod.Server.Data;
using KumasBarkod.Server.Ingest;
using KumasBarkod.Server.Lots;
using KumasBarkod.Server.Shipments;

namespace KumasBarkod.Server.Reports;

/// <summary>
/// İstisnalar (yetkili) ve raporlar. Raporlar her zaman gerçek kayıtlardan hesaplanır (ayrı sayaç yok).
/// Tarih aralığı fabrikanın yerel günüyle (sunucunun saat dilimi): <c>from</c>/<c>to</c> = yyyy-MM-dd, ikisi dahil.
/// </summary>
public sealed class ReportService(SqliteConnectionFactory db, ShipmentService shipments, TimeProvider clock)
{
    public const int MaxDays = 366;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    // ---------- İstisnalar ----------

    public ExceptionsResponse Exceptions(CurrentUser user)
    {
        AuthService.RequireSupervisor(user);
        using var c = db.Open();
        var quarantined = c.Query<ConflictRow>("""
            SELECT k.conflict_id AS ConflictId, k.cut_id AS CutId, coalesce(l.name, r.lot_code_raw) AS ExistingLot,
                   r.length_cm AS ExistingLengthCm, r.qc_created_at AS ExistingCreatedAt, r.station_code AS ExistingStation,
                   k.payload_json AS Payload, k.received_at AS ReceivedAt, r.state AS RollState
            FROM cut_conflicts k JOIN rolls r ON r.cut_id = k.cut_id LEFT JOIN lots l ON l.lot_id = r.lot_id
            WHERE k.resolved_at IS NULL ORDER BY k.conflict_id
            """).Select(k =>
            {
                var incoming = JsonSerializer.Deserialize<CutSyncRow>(k.Payload, ServerApiContract.Json)!;
                return new QuarantineInfo(k.ConflictId, k.CutId, k.ExistingLot, k.ExistingLengthCm, k.ExistingCreatedAt, k.ExistingStation,
                    incoming.LotCode, incoming.LengthCm, incoming.CreatedAt, incoming.StationCode, k.ReceivedAt, k.RollState);
            }).ToList();

        var voidedAfterShip = c.Query<VoidedRow>("""
            SELECT r.cut_id AS CutId, coalesce(l.name, r.lot_code_raw) AS LotName, r.length_cm AS LengthCm,
                   s.shipment_no AS ShipmentNo, s.closed_at AS ShippedAt, r.void_reason AS VoidReason, r.voided_at AS VoidedAt
            FROM rolls r JOIN shipment_items i ON i.cut_id = r.cut_id AND i.status = 'active'
            JOIN shipments s ON s.shipment_id = i.shipment_id AND s.status = 'confirmed'
            LEFT JOIN lots l ON l.lot_id = r.lot_id
            WHERE r.state = 'shipped' AND r.qc_status = 'voided'
            ORDER BY r.voided_at DESC
            """).Select(v => new VoidedAfterShipInfo(v.CutId, v.LotName, v.LengthCm, v.ShipmentNo, v.ShippedAt, v.VoidReason, v.VoidedAt)).ToList();

        var limit = Timestamps.Format(clock.GetUtcNow() - StaleAfter);
        var stale = shipments.List(ShipmentStatuses.Open, 500).Where(s => string.CompareOrdinal(s.LastActivityAt, limit) < 0).ToList();
        var recent = shipments.List("closed", 100).Where(s => s.Status == ShipmentStatuses.Confirmed).Take(30).ToList();
        var unmatched = c.ExecuteScalar<long>("SELECT count(DISTINCT lot_code_raw) FROM rolls WHERE lot_id IS NULL");
        return new ExceptionsResponse(quarantined, voidedAfterShip, stale, recent, unmatched);
    }

    /// <summary>
    /// Karantina çözümü: aynı barkodlu iki toptan sonradan gelen kayıt geçersiz sayılır (o top kalite kontrolde iptal
    /// edilip yeni etiketle kaydedilmiş olmalı). Bu kayıt bir daha gelirse sessizce yok sayılır; QC'deki uyarı kalkar.
    /// Toptaki başka çözülmemiş çakışma yoksa top karantinadan önceki durumuna döner (<c>released</c> hareketi).
    /// </summary>
    public ExceptionsResponse ResolveConflict(long conflictId, ConflictResolveRequest? request, CurrentUser user)
    {
        AuthService.RequireSupervisor(user);
        var note = request?.Note?.Trim() ?? "";
        if (note.Length is < 3 or > 200 || note.Any(char.IsControl))
            throw new ApiException(400, ServerErrorCodes.InvalidRequest, "Ne yapıldığını yazın (ör. \"K1-000005 yeniden etiketlendi: K1-000031\").");
        var now = Timestamps.Format(clock.GetUtcNow());

        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        var row = c.Query<(string CutId, string? ResolvedAt)>(
            "SELECT cut_id, resolved_at FROM cut_conflicts WHERE conflict_id = @conflictId", new { conflictId }, tx).ToList();
        if (row.Count == 0) throw new ApiException(404, "CONFLICT_NOT_FOUND", "Çakışma kaydı bulunamadı.");
        if (row[0].ResolvedAt is not null) throw new ApiException(409, "CONFLICT_RESOLVED", "Bu çakışma zaten çözülmüş.");
        var cutId = row[0].CutId;

        c.Execute("UPDATE cut_conflicts SET resolved_at = @now, resolution = @note, resolved_by = @user WHERE conflict_id = @conflictId",
            new { now, note, user = user.UserId, conflictId }, tx);

        var open = c.ExecuteScalar<long>("SELECT count(*) FROM cut_conflicts WHERE cut_id = @cutId AND resolved_at IS NULL", new { cutId }, tx);
        var state = c.ExecuteScalar<string>("SELECT state FROM rolls WHERE cut_id = @cutId", new { cutId }, tx);
        if (open == 0 && state == RollStates.Quarantined)
        {
            var before = c.ExecuteScalar<string?>("""
                SELECT from_state FROM stock_movements WHERE cut_id = @cutId AND kind = 'quarantined' ORDER BY movement_id DESC LIMIT 1
                """, new { cutId }, tx) ?? RollStates.InStock;
            // Sevkiyata okutulmuşken karantinaya giren top sevkiyattan çıkarılmıştı: stoğa döner.
            if (before == RollStates.Reserved) before = RollStates.InStock;
            c.Execute("UPDATE rolls SET state = @before, updated_at = @now, version = version + 1 WHERE cut_id = @cutId",
                new { before, now, cutId }, tx);
            c.Execute("""
                INSERT INTO stock_movements (cut_id, kind, from_state, to_state, user_id, occurred_at, recorded_at, note)
                VALUES (@cutId, 'released', 'quarantined', @before, @user, @now, @now, @note)
                """, new { cutId, before, user = user.UserId, now, note = $"Karantina çözüldü: {note}" }, tx);
        }
        DataVersion.Bump(c, tx);
        tx.Commit();
        return Exceptions(user);
    }

    // ---------- Raporlar ----------

    public IReadOnlyList<StockByLotRow> StockByLot()
    {
        using var c = db.Open();
        return c.Query<(string Lot, long Unmatched, long Count, long Cm, string? Oldest)>("""
            SELECT coalesce(l.name, r.lot_code_raw), r.lot_id IS NULL, count(*), sum(r.length_cm), min(r.qc_created_at)
            FROM rolls r LEFT JOIN lots l ON l.lot_id = r.lot_id
            WHERE r.state = 'in_stock'
            GROUP BY coalesce(l.name, r.lot_code_raw), r.lot_id IS NULL
            ORDER BY count(*) DESC, 1
            """).Select(x => new StockByLotRow(x.Lot, x.Unmatched == 1, x.Count, x.Cm, x.Oldest)).ToList();
    }

    public IReadOnlyList<DailyShipmentRow> DailyShipments(string? from, string? to)
    {
        var (a, b) = Range(from, to);
        using var c = db.Open();
        var rows = c.Query<(string ClosedAt, long Rolls, long Cm)>("""
            SELECT s.closed_at,
                   (SELECT count(*) FROM shipment_items i WHERE i.shipment_id = s.shipment_id AND i.status = 'active'),
                   (SELECT coalesce(sum(r.length_cm), 0) FROM shipment_items i JOIN rolls r ON r.cut_id = i.cut_id
                     WHERE i.shipment_id = s.shipment_id AND i.status = 'active')
            FROM shipments s WHERE s.status = 'confirmed' AND s.closed_at >= @a AND s.closed_at < @b
            """, new { a, b });
        return rows
            .GroupBy(r => LocalDay(r.ClosedAt))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DailyShipmentRow(g.Key, g.Count(), g.Sum(r => r.Rolls), g.Sum(r => r.Cm)))
            .ToList();
    }

    public IReadOnlyList<ShippedRollRow> ShippedRolls(string? from, string? to)
    {
        var (a, b) = Range(from, to);
        using var c = db.Open();
        return c.Query<ShippedRollRow>("""
            SELECT i.cut_id AS CutId, coalesce(l.name, r.lot_code_raw) AS Lot, r.length_cm AS LengthCm,
                   s.shipment_no AS ShipmentNo, s.closed_at AS ShippedAt, u.name AS ShippedBy
            FROM shipment_items i JOIN shipments s ON s.shipment_id = i.shipment_id
            JOIN rolls r ON r.cut_id = i.cut_id LEFT JOIN lots l ON l.lot_id = r.lot_id JOIN users u ON u.user_id = s.closed_by
            WHERE s.status = 'confirmed' AND i.status = 'active' AND s.closed_at >= @a AND s.closed_at < @b
            ORDER BY s.closed_at, i.item_id
            """, new { a, b }).AsList();
    }

    public IReadOnlyList<ShipmentHistoryRow> ShipmentHistory(string? from, string? to)
    {
        var (a, b) = Range(from, to);
        using var c = db.Open();
        return c.Query<ShipmentHistoryRow>("""
            SELECT s.shipment_no AS ShipmentNo, s.status AS Status,
                   (SELECT group_concat(l.name, ', ') FROM shipment_lots sl JOIN lots l ON l.lot_id = sl.lot_id WHERE sl.shipment_id = s.shipment_id) AS Lots,
                   (SELECT count(*) FROM shipment_items i WHERE i.shipment_id = s.shipment_id AND i.status <> 'removed') AS Rolls,
                   (SELECT coalesce(sum(r.length_cm), 0) FROM shipment_items i JOIN rolls r ON r.cut_id = i.cut_id
                     WHERE i.shipment_id = s.shipment_id AND i.status <> 'removed') AS LengthCm,
                   uo.name AS OpenedBy, s.opened_at AS OpenedAt, uc.name AS ClosedBy, s.closed_at AS ClosedAt,
                   CASE s.status WHEN 'reversed' THEN 'Geri alındı (' || ur.name || '): ' || s.reversal_reason
                                 WHEN 'cancelled' THEN 'İptal: ' || s.close_reason END AS Note
            FROM shipments s JOIN users uo ON uo.user_id = s.opened_by LEFT JOIN users uc ON uc.user_id = s.closed_by
            LEFT JOIN users ur ON ur.user_id = s.reversed_by
            WHERE s.status <> 'open' AND s.closed_at >= @a AND s.closed_at < @b
            ORDER BY s.closed_at DESC, s.shipment_id DESC
            """, new { a, b }).AsList();
    }

    /// <summary>Yerel günler (dahil) → UTC [başlangıç, bitiş).</summary>
    private (string From, string To) Range(string? from, string? to)
    {
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)
            || !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            throw new ApiException(400, ServerErrorCodes.InvalidRequest, "Tarih aralığı yyyy-MM-dd biçiminde olmalı.");
        if (t < f || t.DayNumber - f.DayNumber >= MaxDays)
            throw new ApiException(400, ServerErrorCodes.InvalidRequest, $"Tarih aralığı en fazla {MaxDays} gün olabilir.");
        var tz = clock.LocalTimeZone;
        string Utc(DateOnly d) => Timestamps.Format(TimeZoneInfo.ConvertTimeToUtc(d.ToDateTime(TimeOnly.MinValue), tz));
        return (Utc(f), Utc(t.AddDays(1)));
    }

    private string LocalDay(string utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(Timestamps.Parse(utc), clock.LocalTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class ConflictRow { public long ConflictId { get; init; } public string CutId { get; init; } = ""; public string ExistingLot { get; init; } = ""; public long ExistingLengthCm { get; init; } public string ExistingCreatedAt { get; init; } = ""; public string ExistingStation { get; init; } = ""; public string Payload { get; init; } = ""; public string ReceivedAt { get; init; } = ""; public string RollState { get; init; } = ""; }
    private sealed class VoidedRow { public string CutId { get; init; } = ""; public string LotName { get; init; } = ""; public long LengthCm { get; init; } public string ShipmentNo { get; init; } = ""; public string ShippedAt { get; init; } = ""; public string? VoidReason { get; init; } public string? VoidedAt { get; init; } }
}
