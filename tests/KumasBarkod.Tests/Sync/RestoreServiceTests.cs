using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.Services;
using KumasBarkod.Core.Sync;
using KumasBarkod.Tests.Data;

namespace KumasBarkod.Tests.Sync;

/// <summary>
/// Planın 9. adım testi: veritabanını silip tablodan kur; iptal kayıtları ve sayaç korunmalı,
/// tekrar ID üretilmemeli.
/// </summary>
public class RestoreServiceTests : IDisposable
{
    private const string Url = "https://script.google.com/macros/s/TEST/exec";
    private static readonly SyncTarget Target = new(Url, FakeAppsScript.Key);

    private readonly FakeAppsScript _sheet = new();
    private readonly AppsScriptTransport _transport;
    private readonly FixedClock _clock = new();

    // "Eski bilgisayar": kayıtlar burada oluşur ve tabloya gönderilir
    private readonly TempDatabase _oldDb = new();
    private readonly CutService _oldCuts;
    private readonly SettingsRepository _oldSettings;

    // "Yeni bilgisayar": boş veritabanı
    private readonly TempDatabase _newDb = new();
    private readonly RestoreService _restore;

    public RestoreServiceTests()
    {
        _transport = new AppsScriptTransport(new HttpClient(_sheet));

        DatabaseMigrator.Migrate(_oldDb.Factory);
        _oldSettings = new SettingsRepository(_oldDb.Factory);
        _oldSettings.SetSyncTarget(Url, FakeAppsScript.Key);
        _oldCuts = new CutService(_oldDb.Factory, _oldSettings, _clock);

        DatabaseMigrator.Migrate(_newDb.Factory);
        _restore = new RestoreService(_newDb.Factory, _transport, _clock);
    }

    public void Dispose()
    {
        _oldDb.Dispose();
        _newDb.Dispose();
    }

    private async Task SyncOldToSheet() =>
        await new SyncService(new OutboxRepository(_oldDb.Factory), _oldSettings, new StationCounters(_oldDb.Factory), _transport, _clock).RunOnceAsync();

    private static List<Cut> AllCuts(SqliteConnectionFactory f)
    {
        using var c = f.Open();
        return c.Query<Cut>($"SELECT {Cut.SelectColumns} FROM cuts ORDER BY cut_id").AsList();
    }

    [Fact]
    public async Task Restores_every_field_including_voids_and_prints()
    {
        _oldCuts.CreateCut("LOT-A", 2550, "Ahmet");
        var b = _oldCuts.CreateCut("LOT-ŞĞİ", 1275);
        _oldCuts.CreateCut("LOT-B", 700);
        new PrintService(_oldDb.Factory, new NullPrinter(), _clock).Print("K1-000001");
        _oldCuts.VoidCut(b.CutId, "Yanlış metre");
        _oldSettings.SetStationCode("K2");
        _oldCuts.CreateCut("LOT-C", 100);
        await SyncOldToSheet();

        var result = await _restore.RestoreAsync(Target);

        Assert.Equal(4, result.Cuts);
        Assert.Equal(1, result.Voided);
        var expected = AllCuts(_oldDb.Factory);
        var actual = AllCuts(_newDb.Factory);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.Equivalent(expected[i], actual[i], strict: true);
    }

    [Fact]
    public async Task Counter_jumps_1000_ahead_so_ids_are_never_reused()
    {
        for (var i = 0; i < 5; i++) _oldCuts.CreateCut("LOT-A", 100);
        await SyncOldToSheet();
        // Eski bilgisayarda 3 kayıt daha açıldı ama tabloya GİTMEDEN disk bozuldu:
        for (var i = 0; i < 3; i++) _oldCuts.CreateCut("LOT-A", 100);   // K1-000006..8 (basılı etiketleri var!)

        var result = await _restore.RestoreAsync(Target);

        Assert.Equal(1005, result.Counters["K1"]);
        var newCuts = new CutService(_newDb.Factory, new SettingsRepository(_newDb.Factory), _clock);
        Assert.Equal("K1-001006", newCuts.CreateCut("LOT-A", 100).CutId);   // 6–8 tekrar üretilmez
    }

    [Fact]
    public async Task Counter_is_capped_at_max_sequence()
    {
        using (var c = _oldDb.Factory.Open())
            c.Execute("INSERT INTO station_counter (station_code, last_sequence) VALUES ('K1', 999500)");
        _oldCuts.CreateCut("LOT-A", 100);   // K1-999501
        await SyncOldToSheet();

        var result = await _restore.RestoreAsync(Target);

        Assert.Equal(CutIds.MaxSequence, result.Counters["K1"]);
    }

    [Fact]
    public async Task Restored_cuts_are_not_queued_for_sync()
    {
        _oldCuts.CreateCut("LOT-A", 100);
        await SyncOldToSheet();
        var requestsBefore = _sheet.RequestCount;

        await _restore.RestoreAsync(Target);

        Assert.Equal(0, new OutboxRepository(_newDb.Factory).CountPendingCuts());
        var newSettings = new SettingsRepository(_newDb.Factory);
        newSettings.SetSyncTarget(Url, FakeAppsScript.Key);
        var r = await new SyncService(new OutboxRepository(_newDb.Factory), newSettings, new StationCounters(_newDb.Factory), _transport, _clock).RunOnceAsync();
        Assert.Equal(0, r.SentCuts);
        // Geri kurmanın okuma isteği + tabloya ilk bağlanışta sayaç öğrenme (boş paket); kesim gönderilmedi
        Assert.Equal(requestsBefore + 2, _sheet.RequestCount);
        Assert.Equal(0, _sheet.BatchSizes[^1]);
    }

    [Fact]
    public async Task Large_sheet_is_read_in_pages()
    {
        for (var i = 0; i < 2500; i++) _oldCuts.CreateCut("LOT-A", 100);
        await SyncOldToSheet();

        var progress = new List<string>();
        var result = await _restore.RestoreAsync(Target, new SyncProgress(progress));

        Assert.Equal(2500, result.Cuts);
        Assert.Equal(2, _sheet.ExportRequests);
        Assert.Contains(progress, p => p.Contains("Tamamlandı"));
    }

    // ---------- Güvenlik ----------

    [Fact]
    public async Task Refuses_when_local_database_is_not_empty()
    {
        _oldCuts.CreateCut("LOT-A", 100);
        await SyncOldToSheet();
        new CutService(_newDb.Factory, new SettingsRepository(_newDb.Factory), _clock).CreateCut("YEREL", 100);

        var ex = await Assert.ThrowsAsync<CutValidationException>(() => _restore.RestoreAsync(Target));
        Assert.Contains("boş veritabanına", ex.Message);
        Assert.Single(AllCuts(_newDb.Factory));
    }

    [Theory]
    [InlineData("cut_id", "K1-000099")]       // ID sıra numarasıyla uyuşmuyor
    [InlineData("status", "deleted")]
    [InlineData("length_cm", 0)]
    [InlineData("created_at", "dün")]
    [InlineData("lot_code", null)]
    [InlineData("voided_at", "2026-11-01T09:00:00.000Z")]   // aktif kayıtta iptal zamanı
    public async Task Tampered_sheet_writes_nothing(string column, object? value)
    {
        _oldCuts.CreateCut("LOT-A", 100);
        _oldCuts.CreateCut("LOT-A", 200);
        await SyncOldToSheet();
        _sheet.SetCell("K1-000002", column, value);

        var ex = await Assert.ThrowsAsync<CutValidationException>(() => _restore.RestoreAsync(Target));

        Assert.Contains(column == "cut_id" ? "K1-000099" : "K1-000002", ex.Message);   // hatalı kayıt ID ile bildirilir
        Assert.Empty(AllCuts(_newDb.Factory));
    }

    [Fact]
    public async Task Unreadable_value_in_reply_names_data_problem_not_access_problem()
    {
        _sheet.ForceStatus = System.Net.HttpStatusCode.OK;
        _sheet.ForceBody = """{"ok":true,"total":1,"version":7,"rows":[{"cut_id":"K1-000001","lot_code":2}]}""";

        var ex = await Assert.ThrowsAsync<SyncException>(() => _restore.RestoreAsync(Target));

        Assert.Contains("lot_code", ex.Message);
        Assert.DoesNotContain("Herkes", ex.Message);
        Assert.Empty(AllCuts(_newDb.Factory));
    }

    [Fact]
    public async Task Duplicate_row_in_sheet_writes_nothing()
    {
        _oldCuts.CreateCut("LOT-A", 100);
        await SyncOldToSheet();
        _sheet.Sheet.Add(new Dictionary<string, System.Text.Json.JsonElement>(_sheet.Sheet[0]));

        var ex = await Assert.ThrowsAsync<CutValidationException>(() => _restore.RestoreAsync(Target));
        Assert.Contains("birden fazla", ex.Message);
        Assert.Empty(AllCuts(_newDb.Factory));
    }

    [Fact]
    public async Task Empty_sheet_is_reported()
    {
        var ex = await Assert.ThrowsAsync<CutValidationException>(() => _restore.RestoreAsync(Target));
        Assert.Contains("kayıt yok", ex.Message);
    }

    [Fact]
    public async Task Old_script_version_gives_clear_message()
    {
        _sheet.ExportUnsupported = true;
        var ex = await Assert.ThrowsAsync<SyncException>(() => _restore.RestoreAsync(Target));
        Assert.Contains("güncelleyip", ex.Message);
    }

    [Fact]
    public async Task Wrong_key_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<SyncException>(() => _restore.RestoreAsync(new SyncTarget(Url, "yanlis")));
        Assert.Equal(SyncFailureKind.Rejected, ex.Kind);
    }

    [Fact]
    public void Validate_accepts_a_normal_row()
    {
        var row = new CutSyncRow("K1-000001", "K1", 1, "LOT", 100, null, "active", null, null,
            "2026-11-01T09:00:00.000Z", 1, "2026-11-01T09:00:01.000Z");
        Assert.Null(RestoreService.Validate(row));
    }

    private sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) => sink.Add(value);
    }

    private sealed class NullPrinter : ILabelPrinter
    {
        public string DisplayName => "yok";
        public bool IsPhysical => false;
        public void Print(LabelData label) { }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    }
}
