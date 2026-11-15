using System.Text.Json;
using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Services;
using KumasBarkod.Core.Sync;
using KumasBarkod.Server.Data;
using KumasBarkod.Server.Lots;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Server.Ingest;

/// <summary>İstemcinin düzeltebileceği hata: HTTP 4xx + kod + Türkçe mesaj.</summary>
public sealed class ApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

/// <summary>
/// İstasyonlardan gelen kesim paketlerini stoğa işler. Bütün paket TEK işlemde (BEGIN IMMEDIATE):
/// ya tamamı yazılır ya hiçbiri. Tekrar gönderime dayanıklıdır: aynı paket kaç kez gelirse gelsin
/// sonuç aynıdır (yanıt kaybolup istasyon tekrar gönderdiğinde çift top/hareket oluşmaz).
///
/// Kurallar:
/// - Yeni kesim → top <c>in_stock</c> (etiketi basılan top doğrudan stoktadır) + <c>qc_created</c> hareketi;
///   zaten iptal edilmiş geldiyse ardından <c>qc_voided</c>.
/// - Bilinen kesim, aynı kimlik (istasyon, sıra, lot, uzunluk, operatör, kayıt zamanı) → yalnızca
///   değişebilen alanlar ve yalnızca İLERİ yönde: baskı sayısı büyüğü, iptal bir kez olunca kalıcı.
/// - Bilinen ID, farklı kimlik → iki fiziksel top aynı etiketi taşıyor: ezilmez, <c>cut_conflicts</c>'e
///   yazılır, top <c>quarantined</c> olur (sevk edilmişse durumu değişmez, kayıt yine tutulur).
/// - Bilgisayarın ŞU ANKİ istasyon kodu başka bir bilgisayara kayıtlıysa paket hiç yazılmadan reddedilir.
/// </summary>
public sealed class CutIngestService(SqliteConnectionFactory db, TimeProvider clock, ILogger<CutIngestService> log)
{
    public const int MaxRowsPerBatch = 500;

    public CutsBatchResponse Ingest(CutsBatchRequest request)
    {
        ValidateRequest(request);
        var now = Timestamps.Format(clock.GetUtcNow());

        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);

        // İstasyon kaydı: bu bilgisayarın ŞU ANKİ kodu başka bir bilgisayara aitse paket hiç yazılmaz
        // (iki bilgisayar aynı koddan ID üretmeye devam etmesin; kullanıcı kodu değiştirmeye yönlendirilir).
        ClaimStation(c, tx, request.StationCode, request, now);

        // Paketteki ESKİ kodlar (bilgisayarın kodu sonradan değişmiş olabilir): sahipsizse bu bilgisayara
        // kaydedilir; başkasına aitse kesimler yine işlenir — gerçek toplar sisteme girmeli. ID çakışırsa
        // aşağıdaki çakışma kuralı topu karantinaya alır, sessizce ezilmez.
        foreach (var code in request.Rows.Select(r => r.StationCode).Distinct(StringComparer.Ordinal)
                     .Where(code => code != request.StationCode))
        {
            if (!TryClaimStation(c, tx, code, request, now))
                log.LogWarning("{Install} bilgisayarı, başka bilgisayara kayıtlı {Code} koduyla eski kesim gönderdi",
                    request.InstallId, code);
        }

        var changesBefore = c.ExecuteScalar<long>("SELECT total_changes()", transaction: tx);
        var accepted = 0;
        var conflicts = new List<ServerCutConflict>();
        foreach (var row in request.Rows)
        {
            if (ApplyRow(c, tx, row, request.InstallId, now) is { } conflict)
                conflicts.Add(conflict);
            else
                accepted++;
        }

        // Top/hareket/çakışma yazıldıysa ekranlar yenilensin (yalnızca istasyon "son görülme" ise değil).
        if (c.ExecuteScalar<long>("SELECT total_changes()", transaction: tx) > changesBefore)
            DataVersion.Bump(c, tx);

        var maxSequences = c.Query<(string Station, long Max)>(
                "SELECT station_code, max(sequence_no) FROM rolls GROUP BY station_code", transaction: tx)
            .ToDictionary(x => x.Station, x => x.Max, StringComparer.Ordinal);

        tx.Commit();

        if (conflicts.Count > 0)
            log.LogWarning("Barkod çakışması: {Count} kesim karantinada ({First}, istasyon {Station})",
                conflicts.Count, conflicts[0].CutId, request.StationCode);

        return new CutsBatchResponse(true, ServerApiContract.ApiVersion, accepted, conflicts, maxSequences);
    }

    private static void ValidateRequest(CutsBatchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.InstallId) || request.InstallId.Length > 100)
            throw Invalid("Bilgisayar kimliği eksik.");
        if (!Stations.IsValidCode(request.StationCode))
            throw Invalid($"İstasyon kodu geçersiz: '{request.StationCode}'.");
        if (request.Rows is null)
            throw Invalid("Kesim listesi eksik.");
        if (request.Rows.Count > MaxRowsPerBatch)
            throw Invalid($"Bir pakette en fazla {MaxRowsPerBatch} kesim gönderilebilir.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in request.Rows)
        {
            if (row is null) throw Invalid("Pakette boş satır var.");
            var error = RestoreService.Validate(row)
                ?? (row.LotCode.Length > CutService.MaxLotLength ? "lot çok uzun" : null);
            if (error is not null)
                throw Invalid($"{row.CutId ?? "ID'siz kesim"}: {error}. Paket yazılmadı.");
            if (!seen.Add(row.CutId))
                throw Invalid($"{row.CutId} pakette iki kez var. Paket yazılmadı.");
        }
    }

    private static ApiException Invalid(string message) => new(400, ServerErrorCodes.InvalidRequest, message);

    private static void ClaimStation(SqliteConnection c, SqliteTransaction tx, string code, CutsBatchRequest request, string now)
    {
        if (!TryClaimStation(c, tx, code, request, now))
        {
            throw new ApiException(409, ServerErrorCodes.StationOwnedByOtherDevice,
                $"{code} istasyon kodu depo sunucusunda başka bir bilgisayara kayıtlı. Bu bilgisayara Ayarlar'dan " +
                "farklı bir istasyon kodu verin. Bilgisayar değiştirildiyse yönetici sunucuda eski kaydı serbest bırakmalı. " +
                "Kayıtlar bu bilgisayarda bekliyor, kaybolmadı.");
        }
    }

    /// <returns>Kod bu bilgisayara ait (ya da şimdi kaydedildi) ise true; başka bilgisayara aitse false.</returns>
    private static bool TryClaimStation(SqliteConnection c, SqliteTransaction tx, string code, CutsBatchRequest request, string now)
    {
        var owner = c.ExecuteScalar<string?>(
            "SELECT install_id FROM stations WHERE station_code = @code", new { code }, tx);

        if (owner is null)
        {
            c.Execute("""
                INSERT INTO stations (station_code, install_id, app_version, registered_at, last_seen_at)
                VALUES (@code, @installId, @appVersion, @now, @now)
                """, new { code, installId = request.InstallId, appVersion = request.AppVersion, now }, tx);
            return true;
        }
        if (owner != request.InstallId) return false;

        c.Execute("UPDATE stations SET last_seen_at = @now, app_version = @appVersion WHERE station_code = @code",
            new { code, appVersion = request.AppVersion, now }, tx);
        return true;
    }

    private sealed record ExistingRoll(
        string StationCode, long SequenceNo, string LotCodeRaw, long LengthCm, string? Operator,
        string QcCreatedAt, string QcStatus, long PrintCount, string? LastPrintedAt, string State);

    /// <returns>Çakışma varsa ayrıntısı; yoksa null.</returns>
    private ServerCutConflict? ApplyRow(SqliteConnection c, SqliteTransaction tx, CutSyncRow row, string installId, string now)
    {
        var existing = c.QuerySingleOrDefault<ExistingRoll>("""
            SELECT station_code AS StationCode, sequence_no AS SequenceNo, lot_code_raw AS LotCodeRaw,
                   length_cm AS LengthCm, operator AS Operator, qc_created_at AS QcCreatedAt,
                   qc_status AS QcStatus, print_count AS PrintCount, last_printed_at AS LastPrintedAt, state AS State
            FROM rolls WHERE cut_id = @CutId
            """, new { row.CutId }, tx);

        if (existing is null)
        {
            InsertRoll(c, tx, row, now);
            return null;
        }

        var sameCut = existing.StationCode == row.StationCode && existing.SequenceNo == row.SequenceNo
            && existing.LotCodeRaw == row.LotCode && existing.LengthCm == row.LengthCm
            && existing.Operator == row.Operator && existing.QcCreatedAt == row.CreatedAt;

        if (!sameCut)
        {
            RecordConflict(c, tx, row, existing, installId, now);
            return new ServerCutConflict(row.CutId, row.CreatedAt, existing.QcCreatedAt);
        }

        // Değişebilen alanlar yalnızca ileri: baskı sayısı azalmaz, son baskı geriye gitmez.
        var printCount = Math.Max(existing.PrintCount, row.PrintCount);
        var lastPrinted = Later(existing.LastPrintedAt, row.LastPrintedAt);
        if (printCount != existing.PrintCount || lastPrinted != existing.LastPrintedAt)
        {
            c.Execute("""
                UPDATE rolls SET print_count = @printCount, last_printed_at = @lastPrinted,
                                 updated_at = @now, version = version + 1
                WHERE cut_id = @CutId
                """, new { row.CutId, printCount, lastPrinted, now }, tx);
        }

        if (row.Status == CutStatus.Voided && existing.QcStatus != CutStatus.Voided)
            ApplyVoid(c, tx, row, existing.State, now);

        return null;
    }

    private static void InsertRoll(SqliteConnection c, SqliteTransaction tx, CutSyncRow row, string now)
    {
        c.Execute("""
            INSERT INTO rolls (cut_id, station_code, sequence_no, lot_code_raw, length_cm, operator, qc_status,
                               void_reason, voided_at, qc_created_at, print_count, last_printed_at,
                               state, first_seen_at, updated_at, lot_id)
            VALUES (@CutId, @StationCode, @SequenceNo, @LotCode, @LengthCm, @Operator, 'active',
                    NULL, NULL, @CreatedAt, @PrintCount, @LastPrintedAt,
                    'in_stock', @now, @now, @lotId)
            """, new { row.CutId, row.StationCode, row.SequenceNo, row.LotCode, row.LengthCm, row.Operator,
                       row.CreatedAt, row.PrintCount, row.LastPrintedAt, now,
                       lotId = LotService.Resolve(c, tx, row.LotCode) }, tx);

        Movement(c, tx, row.CutId, MovementKinds.QcCreated, null, RollStates.InStock, row.CreatedAt, now,
            row.StationCode, note: null);

        // Sunucuya ulaşmadan iptal edilmiş kesim: geçmişte önce stoğa girmiş, sonra iptal edilmiş görünür
        // (gerçekte de öyle oldu: kayıt açıldı, sonra iptal edildi).
        if (row.Status == CutStatus.Voided)
            ApplyVoid(c, tx, row, RollStates.InStock, now);
    }

    private static void ApplyVoid(SqliteConnection c, SqliteTransaction tx, CutSyncRow row, string currentState, string now)
    {
        // Yalnızca stoktaki top 'voided' olur. Sevk edilmiş / karantinadaki / sevkiyatta okutulmuş topun
        // durumu değişmez (tıra yüklenmiş top "iptal" görünüp stoktan sessizce kaybolmasın); iptal bilgisi
        // kayda ve harekete yazılır, yetkili ekranında görünür.
        var newState = currentState == RollStates.InStock ? RollStates.Voided : currentState;
        c.Execute("""
            UPDATE rolls SET qc_status = 'voided', void_reason = @VoidReason, voided_at = @VoidedAt,
                             state = @newState, updated_at = @now, version = version + 1
            WHERE cut_id = @CutId
            """, new { row.CutId, row.VoidReason, row.VoidedAt, newState, now }, tx);

        var note = newState == currentState
            ? $"QC'de iptal edildi ({row.VoidReason}); top '{currentState}' durumundayken — yetkili kontrol etmeli"
            : row.VoidReason;
        Movement(c, tx, row.CutId, MovementKinds.QcVoided, currentState, newState, row.VoidedAt!, now, row.StationCode, note);
    }

    private static void RecordConflict(SqliteConnection c, SqliteTransaction tx, CutSyncRow row, ExistingRoll existing,
        string installId, string now)
    {
        var payload = JsonSerializer.Serialize(row, ServerApiContract.Json);
        var inserted = c.Execute("""
            INSERT INTO cut_conflicts (cut_id, created_at, station_code, install_id, payload_json, received_at)
            VALUES (@CutId, @CreatedAt, @StationCode, @installId, @payload, @now)
            ON CONFLICT (cut_id, created_at, payload_json) DO NOTHING
            """, new { row.CutId, row.CreatedAt, row.StationCode, installId, payload, now }, tx);

        // Aynı çakışma tekrar geldiyse (istasyon kuyruğunda bekliyor) yeni hareket yazılmaz.
        if (inserted == 0) return;

        if (existing.State is not (RollStates.Quarantined or RollStates.Shipped))
        {
            c.Execute("""
                UPDATE rolls SET state = 'quarantined', updated_at = @now, version = version + 1 WHERE cut_id = @CutId
                """, new { row.CutId, now }, tx);
            Movement(c, tx, row.CutId, MovementKinds.Quarantined, existing.State, RollStates.Quarantined, now, now,
                row.StationCode, $"Aynı ID ile başka bir top geldi (kayıt zamanı {row.CreatedAt}, lot {row.LotCode})");
        }
    }

    private static void Movement(SqliteConnection c, SqliteTransaction tx, string cutId, string kind,
        string? from, string to, string occurredAt, string recordedAt, string? station, string? note) =>
        c.Execute("""
            INSERT INTO stock_movements (cut_id, kind, from_state, to_state, station_code, occurred_at, recorded_at, note)
            VALUES (@cutId, @kind, @from, @to, @station, @occurredAt, @recordedAt, @note)
            """, new { cutId, kind, from, to, station, occurredAt, recordedAt, note }, tx);

    private static string? Later(string? a, string? b) =>
        a is null ? b : b is null ? a : string.CompareOrdinal(a, b) >= 0 ? a : b;
}
