using System.Globalization;
using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;
using KumasBarkod.Server.Auth;
using KumasBarkod.Server.Ingest;
using KumasBarkod.Server.Lots;

namespace KumasBarkod.Server.Sheets;

/// <summary>
/// Google Sheets kopyası depo sunucusundan (Faz 6). Patron fabrika dışından telefondan bakar; asıl kayıt sunucuda.
/// <list type="bullet">
/// <item><b>Kesimler</b>: istasyonların gönderdiği sayfanın aynısı (aynı sütunlar, aynı Code.gs mantığı: cut_id +
/// oluşturma zamanı ile güncelle). Kuyruk <c>sheets_pending</c>'i veritabanı tetikleyicileri doldurur.</item>
/// <item><b>Stok</b> ve <b>Sevkiyatlar</b>: veri değiştikçe (en sık <see cref="SnapshotInterval"/>'da bir) baştan yazılır.</item>
/// </list>
/// İnternet yoksa sevkiyat ve QC etkilenmez; kuyruk bekler, bağlantı gelince kendiliğinden gider.
/// </summary>
public sealed class SheetsSyncService(
    SqliteConnectionFactory db,
    AppsScriptTransport transport,
    TimeProvider clock,
    ILogger<SheetsSyncService> log)
{
    public const int BatchSize = 200;
    public static TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromMinutes(2);

    private const string UrlKey = "sheets_url";
    private const string SecretKey = "sheets_key";
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private readonly SemaphoreSlim _run = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private long _snapshotVersion = -1;
    private DateTimeOffset _lastSnapshotAttempt = DateTimeOffset.MinValue;
    private string? _lastSuccessAt, _lastSnapshotAt, _lastError, _lastErrorAt;

    // ---------- Ayarlar (yetkili) ----------

    public SheetsStatusResponse Status(CurrentUser actor)
    {
        AuthService.RequireSupervisor(actor);
        return BuildStatus();
    }

    /// <summary>
    /// Kaydetmeden önce Google'a bağlanıp anahtarı ve script sürümünü (v8) dener: yanlış ayar kaydedilmez.
    /// Adres boş = Sheets kopyası kapatılır. Anahtar boş = kayıtlı anahtar korunur.
    /// </summary>
    public async Task<SheetsStatusResponse> SaveAsync(SheetsSettingsRequest? request, CurrentUser actor, CancellationToken ct)
    {
        AuthService.RequireSupervisor(actor);
        var url = request?.Url?.Trim() ?? "";
        var current = Target();
        if (url.Length == 0)
        {
            using var c = db.Open();
            c.Execute("DELETE FROM server_settings WHERE key IN (@UrlKey, @SecretKey)", new { UrlKey, SecretKey });
            log.LogInformation("Sheets kopyası kapatıldı ({User})", actor.Name);
            ClearError();
            return BuildStatus();
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ApiException(400, ServerErrorCodes.InvalidRequest, "Sheets adresi https:// ile başlayan Apps Script web uygulaması adresi olmalı.");
        var key = string.IsNullOrWhiteSpace(request?.Key) ? current?.Key : request.Key.Trim();
        if (string.IsNullOrEmpty(key))
            throw new ApiException(400, ServerErrorCodes.InvalidRequest, "Apps Script anahtarını (API_KEY) yazın.");

        var target = new SyncTarget(url, key);
        int version;
        try { version = await transport.GetScriptVersionAsync(target, ct); }
        catch (SyncException ex)
        {
            throw new ApiException(400, SheetsErrorCode, "Kaydedilmedi: " + ex.Message);
        }
        if (version < AppsScriptTransport.SnapshotScriptVersion)
            throw new ApiException(400, SheetsErrorCode,
                $"Kaydedilmedi: Apps Script eski sürüm (v{version}). Depo sunucusu v{AppsScriptTransport.SnapshotScriptVersion} ister: " +
                "Code.gs'yi güncelleyip yeniden dağıtın (KURULUM.md).");

        using (var c = db.Open())
        using (var tx = c.BeginTransaction())
        {
            c.Execute("INSERT OR REPLACE INTO server_settings (key, value) VALUES (@UrlKey, @url), (@SecretKey, @key)",
                new { UrlKey, SecretKey, url, key }, tx);
            if (current?.Url != url)
            {
                // Başka bir tabloya geçildi: bütün toplar oraya da gitsin.
                c.Execute("""
                    INSERT OR REPLACE INTO sheets_pending (cut_id, seq)
                    SELECT cut_id, (SELECT coalesce(max(seq), 0) FROM sheets_pending) + row_number() OVER (ORDER BY cut_id) FROM rolls
                    """, transaction: tx);
            }
            tx.Commit();
        }
        log.LogInformation("Sheets kopyası ayarlandı ({User}), Apps Script v{Version}", actor.Name, version);
        _snapshotVersion = -1;
        _lastSnapshotAttempt = DateTimeOffset.MinValue;
        ClearError();
        Wake();
        return BuildStatus();
    }

    public const string SheetsErrorCode = "SHEETS_FAILED";

    // ---------- Gönderim (SheetsWorker çağırır) ----------

    public void Wake() => _wake.Release();

    /// <summary>Ayar değişikliği gelene kadar (en fazla <paramref name="timeout"/>) bekler.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        await _wake.WaitAsync(timeout, ct);
        while (_wake.CurrentCount > 0) await _wake.WaitAsync(0, ct);
    }

    /// <summary>Kuyruğu boşaltır, gerekirse Stok/Sevkiyatlar'ı yazar. Hata olursa false (durumda görünür).</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await _run.WaitAsync(ct);
        try
        {
            var target = Target();
            if (target is null) return true;
            try
            {
                await DrainAsync(target, ct);
                await SnapshotIfDueAsync(target, ct);
                _lastSuccessAt = Timestamps.Format(clock.GetUtcNow());
                if (_lastError is not null)
                {
                    log.LogInformation("Sheets kopyası yeniden çalışıyor");
                    ClearError();
                }
                return true;
            }
            catch (SyncException ex)
            {
                if (ex.Message != _lastError)   // aynı hata her denemede günlüğü doldurmasın
                    log.LogWarning("Sheets kopyası gönderilemedi: {Message}", ex.Message);
                _lastError = ex.Message;
                _lastErrorAt = Timestamps.Format(clock.GetUtcNow());
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Hizmet durmasın (sevkiyat Sheets'e bağlı değil); günlüğe yazılır, sonra yeniden denenir.
                log.LogError(ex, "Sheets kopyasında beklenmeyen hata");
                _lastError = "Beklenmeyen hata (sunucu günlüğüne yazıldı).";
                _lastErrorAt = Timestamps.Format(clock.GetUtcNow());
                return false;
            }
        }
        finally
        {
            _run.Release();
        }
    }

    private async Task DrainAsync(SyncTarget target, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            List<PendingRow> batch;
            using (var c = db.Open())
            {
                batch = c.Query<PendingRow>("""
                    SELECT p.cut_id AS CutId, p.seq AS Seq, r.station_code AS StationCode, r.sequence_no AS SequenceNo,
                           r.lot_code_raw AS LotCode, r.length_cm AS LengthCm, r.operator AS Operator, r.qc_status AS Status,
                           r.void_reason AS VoidReason, r.voided_at AS VoidedAt, r.qc_created_at AS CreatedAt,
                           r.print_count AS PrintCount, r.last_printed_at AS LastPrintedAt
                    FROM sheets_pending p JOIN rolls r ON r.cut_id = p.cut_id
                    ORDER BY p.seq LIMIT @BatchSize
                    """, new { BatchSize }).AsList();
            }
            if (batch.Count == 0) return;

            var result = await transport.SendAsync(target, batch.Select(b => b.ToRow()).ToList(), ct);
            if (result.Conflicts.Count > 0)
                log.LogWarning("Sheets'te aynı barkodlu farklı kayıt var ({Count}): {First}. Sunucudaki kayıt esastır; tablodaki satırı kontrol edin.",
                    result.Conflicts.Count, string.Join(", ", result.Conflicts.Take(5).Select(x => x.CutId)));

            using (var c = db.Open())
            using (var tx = c.BeginTransaction())
            {
                // Gönderim sürerken değişen top kuyrukta kalır (seq büyüdü) ve sonraki turda yeniden gider.
                foreach (var b in batch)
                    c.Execute("DELETE FROM sheets_pending WHERE cut_id = @CutId AND seq = @Seq", new { b.CutId, b.Seq }, tx);
                tx.Commit();
            }
            if (batch.Count < BatchSize) return;
        }
    }

    private async Task SnapshotIfDueAsync(SyncTarget target, CancellationToken ct)
    {
        long version;
        using (var c = db.Open()) version = DataVersion.Get(c);
        var now = clock.GetUtcNow();
        if (version == _snapshotVersion || now - _lastSnapshotAttempt < SnapshotInterval) return;
        _lastSnapshotAttempt = now;

        var updated = Local(Timestamps.Format(now));
        var (stockHeaders, stockRows) = StockSheet();
        await transport.SnapshotAsync(target, "Stok", stockHeaders, stockRows, updated, ct);
        var (shipHeaders, shipRows) = ShipmentsSheet();
        await transport.SnapshotAsync(target, "Sevkiyatlar", shipHeaders, shipRows, updated, ct);

        _snapshotVersion = version;
        _lastSnapshotAt = Timestamps.Format(now);
    }

    /// <summary>Depodaki toplar (stokta + açık sevkiyatta okutulmuş). Metre sayı: tabloda toplanabilir.</summary>
    internal (string[] Headers, List<object?[]> Rows) StockSheet()
    {
        using var c = db.Open();
        var rows = c.Query<StockRow>("""
            SELECT r.cut_id AS CutId, l.name AS LotName, r.lot_code_raw AS Raw, r.length_cm AS LengthCm, r.state AS State,
                   (SELECT s.shipment_no FROM shipment_items i JOIN shipments s ON s.shipment_id = i.shipment_id
                     WHERE i.cut_id = r.cut_id AND i.status = 'active') AS ShipmentNo,
                   r.qc_created_at AS CreatedAt, r.station_code AS Station
            FROM rolls r LEFT JOIN lots l ON l.lot_id = r.lot_id
            WHERE r.state IN ('in_stock', 'reserved')
            ORDER BY coalesce(l.name, r.lot_code_raw) COLLATE NOCASE, r.cut_id
            """).Select(r => new object?[]
            {
                r.CutId, r.LotName ?? r.Raw, Meters(r.LengthCm),
                r.State == "reserved" ? "Sevkiyatta (onay bekliyor)" : "Stokta",
                r.ShipmentNo ?? "", Local(r.CreatedAt), r.Station, r.LotName is null ? "lot tanımsız" : "",
            }).ToList();
        return (["Barkod", "Lot / Firma", "Metre", "Durum", "Sevkiyat", "Kalite kontrol", "İstasyon", "Not"], rows);
    }

    /// <summary>Bütün sevkiyatlar, en yenisi üstte (açık olanlar dahil).</summary>
    internal (string[] Headers, List<object?[]> Rows) ShipmentsSheet()
    {
        using var c = db.Open();
        var rows = c.Query<ShipmentRow>("""
            SELECT s.shipment_no AS No, s.status AS Status,
                   (SELECT group_concat(l.name, ', ') FROM shipment_lots sl JOIN lots l ON l.lot_id = sl.lot_id WHERE sl.shipment_id = s.shipment_id) AS Lots,
                   (SELECT count(*) FROM shipment_items i WHERE i.shipment_id = s.shipment_id AND i.status <> 'removed') AS Rolls,
                   (SELECT coalesce(sum(r.length_cm), 0) FROM shipment_items i JOIN rolls r ON r.cut_id = i.cut_id
                     WHERE i.shipment_id = s.shipment_id AND i.status <> 'removed') AS LengthCm,
                   uo.name AS OpenedBy, s.opened_at AS OpenedAt, uc.name AS ClosedBy, s.closed_at AS ClosedAt,
                   CASE s.status WHEN 'reversed' THEN 'Geri alındı (' || ur.name || '): ' || s.reversal_reason
                                 WHEN 'cancelled' THEN 'İptal: ' || s.close_reason END AS Note
            FROM shipments s JOIN users uo ON uo.user_id = s.opened_by LEFT JOIN users uc ON uc.user_id = s.closed_by
            LEFT JOIN users ur ON ur.user_id = s.reversed_by
            ORDER BY s.opened_at DESC, s.shipment_id DESC
            """).Select(s => new object?[]
            {
                s.No, ShipmentStatuses.Display(s.Status), s.Lots ?? "", s.Rolls, Meters(s.LengthCm), s.OpenedBy, Local(s.OpenedAt),
                s.ClosedBy ?? "", s.ClosedAt is null ? "" : Local(s.ClosedAt), s.Note ?? "",
            }).ToList();
        return (["Sevkiyat", "Durum", "Lot / Firma", "Top", "Metre", "Açan", "Açılış", "Kapatan", "Kapanış", "Not"], rows);
    }

    private static double Meters(long cm) => cm / 100.0;

    private string Local(string utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(Timestamps.Parse(utc), clock.LocalTimeZone).ToString("dd.MM.yyyy HH:mm", Turkish);

    private SyncTarget? Target()
    {
        using var c = db.Open();
        var map = c.Query<(string Key, string Value)>("SELECT key, value FROM server_settings WHERE key IN (@UrlKey, @SecretKey)",
            new { UrlKey, SecretKey }).ToDictionary(x => x.Key, x => x.Value);
        return map.TryGetValue(UrlKey, out var url) && map.TryGetValue(SecretKey, out var key) ? new SyncTarget(url, key) : null;
    }

    private SheetsStatusResponse BuildStatus()
    {
        var target = Target();
        using var c = db.Open();
        var pending = c.ExecuteScalar<long>("SELECT count(*) FROM sheets_pending");
        return new SheetsStatusResponse(target is not null, target?.Url, target is not null, pending,
            _lastSuccessAt, _lastSnapshotAt, _lastError, _lastErrorAt);
    }

    private void ClearError() => (_lastError, _lastErrorAt) = (null, null);

    private sealed class StockRow
    {
        public string CutId { get; init; } = "";
        public string? LotName { get; init; }
        public string Raw { get; init; } = "";
        public long LengthCm { get; init; }
        public string State { get; init; } = "";
        public string? ShipmentNo { get; init; }
        public string CreatedAt { get; init; } = "";
        public string Station { get; init; } = "";
    }

    private sealed class ShipmentRow
    {
        public string No { get; init; } = "";
        public string Status { get; init; } = "";
        public string? Lots { get; init; }
        public long Rolls { get; init; }
        public long LengthCm { get; init; }
        public string OpenedBy { get; init; } = "";
        public string OpenedAt { get; init; } = "";
        public string? ClosedBy { get; init; }
        public string? ClosedAt { get; init; }
        public string? Note { get; init; }
    }

    private sealed class PendingRow
    {
        public string CutId { get; init; } = "";
        public long Seq { get; init; }
        public string StationCode { get; init; } = "";
        public long SequenceNo { get; init; }
        public string LotCode { get; init; } = "";
        public long LengthCm { get; init; }
        public string? Operator { get; init; }
        public string Status { get; init; } = "";
        public string? VoidReason { get; init; }
        public string? VoidedAt { get; init; }
        public string CreatedAt { get; init; } = "";
        public long PrintCount { get; init; }
        public string? LastPrintedAt { get; init; }

        public CutSyncRow ToRow() => new(CutId, StationCode, SequenceNo, LotCode, LengthCm, Operator, Status,
            VoidReason, VoidedAt, CreatedAt, PrintCount, LastPrintedAt);
    }
}

/// <summary>Sheets kopyasını arka planda gönderir: 15 sn'de bir; hata olursa 1 dk bekler. Ayar değişince hemen.</summary>
public sealed class SheetsWorker(SheetsSyncService sheets) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ok = await sheets.RunOnceAsync(stoppingToken);
                await sheets.WaitAsync(ok ? Interval : RetryDelay, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
