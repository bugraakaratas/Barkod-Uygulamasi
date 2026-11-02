using System.Text.Json;
using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.Services;
using KumasBarkod.Core.Sync;
using KumasBarkod.Tests.Data;

namespace KumasBarkod.Tests.Sync;

public class SyncServiceTests : IDisposable
{
    private const string Url = "https://script.google.com/macros/s/TEST/exec";

    private readonly TempDatabase _db = new();
    private readonly FakeAppsScript _sheet = new();
    private readonly SettableClock _clock = new();
    private readonly SettingsRepository _settings;
    private readonly CutService _cuts;
    private readonly OutboxRepository _outbox;
    private readonly SyncService _sync;

    public SyncServiceTests()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        _settings = new SettingsRepository(_db.Factory);
        _settings.SetSyncTarget(Url, FakeAppsScript.Key);
        _cuts = new CutService(_db.Factory, _settings, _clock);
        _outbox = new OutboxRepository(_db.Factory);
        _sync = new SyncService(_outbox, _settings, new StationCounters(_db.Factory), new AppsScriptTransport(new HttpClient(_sheet)), _clock);
    }

    public void Dispose() => _db.Dispose();

    private Task<SyncRunResult> Run() => _sync.RunOnceAsync();

    // ---------- Temel gönderim ----------

    [Fact]
    public async Task Pending_cuts_are_sent_once_and_marked()
    {
        _cuts.CreateCut("LOT-A", 2550);
        _cuts.CreateCut("LOT-B", 1000);

        var r = await Run();

        Assert.True(r.Succeeded);
        Assert.Equal(2, r.SentCuts);
        Assert.Equal(2, _sheet.Sheet.Count);
        Assert.Equal(0, _outbox.CountPendingCuts());

        // İkinci tur: gönderilecek bir şey yok, istek de atılmaz
        var before = _sheet.RequestCount;
        Assert.Equal(0, (await Run()).SentCuts);
        Assert.Equal(before, _sheet.RequestCount);
    }

    [Fact]
    public async Task Payload_has_all_cut_fields_in_snake_case()
    {
        _cuts.CreateCut("LOT-A", 2550, "Ahmet");
        await Run();

        var row = _sheet.Row("K1-000001");
        Assert.Equal("K1", row["station_code"].GetString());
        Assert.Equal(1, row["sequence_no"].GetInt64());
        Assert.Equal("LOT-A", row["lot_code"].GetString());
        Assert.Equal(2550, row["length_cm"].GetInt64());
        Assert.Equal("Ahmet", row["operator"].GetString());
        Assert.Equal("active", row["status"].GetString());
        Assert.Equal(JsonValueKind.Null, row["void_reason"].ValueKind);   // null alanlar da gider
        Assert.Equal(0, row["print_count"].GetInt64());
        Assert.Equal(12, row.Count);                                        // cuts tablosunun tüm alanları

        using var body = JsonDocument.Parse(_sheet.LastBody!);
        Assert.Equal(FakeAppsScript.Key, body.RootElement.GetProperty("key").GetString());
    }

    [Fact]
    public async Task Large_backlog_is_sent_in_batches_of_200()
    {
        for (var i = 0; i < 450; i++) _cuts.CreateCut("LOT-A", 100);

        var r = await Run();

        Assert.Equal(450, r.SentCuts);
        Assert.Equal([200, 200, 50], _sheet.BatchSizes);
        Assert.Equal(450, _sheet.Sheet.Count);
    }

    [Fact]
    public async Task Not_configured_sends_nothing_and_keeps_queue()
    {
        _settings.SetSyncTarget(null, null);
        _cuts.CreateCut("LOT-A", 100);

        var r = await Run();

        Assert.False(r.Configured);
        Assert.Equal(0, _sheet.RequestCount);
        Assert.Equal(1, _outbox.CountPendingCuts());
    }

    // ---------- Kesinti ve tekrar ----------

    [Fact]
    public async Task Offline_keeps_queue_counts_attempts_then_recovers()
    {
        _cuts.CreateCut("LOT-A", 100);
        _cuts.CreateCut("LOT-A", 200);
        _sheet.FailNextAsOffline = 2;

        var r1 = await Run();
        var r2 = await Run();

        Assert.Equal(SyncFailureKind.Offline, r1.Failure!.Kind);
        Assert.Equal(SyncFailureKind.Offline, r2.Failure!.Kind);
        Assert.Empty(_sheet.Sheet);
        Assert.Equal(2, _outbox.CountPendingCuts());
        using (var c = _db.Factory.Open())
        {
            Assert.Equal(2L, c.ExecuteScalar<long>("SELECT max(attempts) FROM sync_outbox"));
            Assert.Contains("ulaşılamıyor", c.ExecuteScalar<string>("SELECT last_error FROM sync_outbox LIMIT 1"));
        }

        var r3 = await Run();
        Assert.True(r3.Succeeded);
        Assert.Equal(2, _sheet.Sheet.Count);
        Assert.Equal(0, _outbox.CountPendingCuts());
    }

    /// <summary>
    /// Planın ana senaryosu: tablo yazıldı ama yanıt kayboldu → uygulama aynı paketi tekrar
    /// gönderir → her kesim yine TEK satır.
    /// </summary>
    [Fact]
    public async Task Lost_response_resend_does_not_duplicate_rows()
    {
        for (var i = 0; i < 5; i++) _cuts.CreateCut("LOT-A", 100 + i);
        _sheet.LoseNextResponse = 1;

        var r1 = await Run();
        Assert.False(r1.Succeeded);
        Assert.Equal(5, _sheet.Sheet.Count);          // tabloya yazılmıştı
        Assert.Equal(5, _outbox.CountPendingCuts());   // ama uygulama bilmiyor

        var r2 = await Run();
        Assert.True(r2.Succeeded);
        Assert.Equal(5, _sheet.Sheet.Count);
        Assert.All(_sheet.Sheet, row => Assert.Equal(1, _sheet.RowCount(row["cut_id"].GetString()!)));
    }

    [Fact]
    public async Task Failure_mid_backlog_keeps_order_and_resumes()
    {
        for (var i = 0; i < 450; i++) _cuts.CreateCut("LOT-A", 100);

        // 1. paket gider, 2. pakette bağlantı kopar
        var calls = 0;
        _sheet.DuringRequest = () => { if (++calls == 2) _sheet.FailNextAsOffline = 1; };
        var r1 = await Run();
        Assert.Equal(200, r1.SentCuts);
        Assert.Equal(250, _outbox.CountPendingCuts());

        _sheet.DuringRequest = null;
        var r2 = await Run();
        Assert.Equal(250, r2.SentCuts);
        Assert.Equal(450, _sheet.Sheet.Count);
        Assert.Equal(Enumerable.Range(1, 450).Select(i => $"K1-{i:D6}"), _sheet.Sheet.Select(r => r["cut_id"].GetString()));
    }

    // ---------- Değişiklikler ----------

    [Fact]
    public async Task Void_and_print_update_the_same_row()
    {
        var cut = _cuts.CreateCut("LOT-A", 100);
        await Run();

        var print = new PrintService(_db.Factory, new NullPrinter(), _clock);
        print.Print(cut.CutId);
        _cuts.VoidCut(cut.CutId, "Yanlış metre");
        Assert.Equal(1, _outbox.CountPendingCuts());   // iki değişiklik, tek bekleyen kesim

        await Run();

        Assert.Single(_sheet.Sheet);
        var row = _sheet.Row(cut.CutId);
        Assert.Equal("voided", row["status"].GetString());
        Assert.Equal("Yanlış metre", row["void_reason"].GetString());
        Assert.Equal(1, row["print_count"].GetInt64());
        Assert.Equal(1, _sheet.BatchSizes[^1]);       // kesim bir kez, son hâliyle gitti
    }

    [Fact]
    public async Task Change_during_send_is_not_lost()
    {
        var cut = _cuts.CreateCut("LOT-A", 100);

        // Paket yoldayken kesim iptal ediliyor: giden paket eski (aktif) hâli taşır.
        // İptalin kuyruk satırı "gönderildi" sayılmamalı ve aynı turun sonraki paketiyle gitmeli.
        string? statusInFirstRequest = null;
        _sheet.DuringRequest = () =>
        {
            _sheet.DuringRequest = null;
            statusInFirstRequest = JsonDocument.Parse(_sheet.LastBody!).RootElement
                .GetProperty("rows")[0].GetProperty("status").GetString();
            _cuts.VoidCut(cut.CutId, "Gönderim sırasında");
        };

        await Run();

        Assert.Equal("active", statusInFirstRequest);
        Assert.Equal(2, _sheet.RequestCount);                                   // ikinci paket iptali taşıdı
        Assert.Equal("voided", _sheet.Row(cut.CutId)["status"].GetString());
        Assert.Single(_sheet.Sheet);
        Assert.Equal(0, _outbox.CountPendingCuts());
    }

    // ---------- Hatalı yanıtlar ----------

    [Fact]
    public async Task Wrong_key_is_rejected_not_offline()
    {
        _settings.SetSyncTarget(Url, "yanlis");
        _cuts.CreateCut("LOT-A", 100);

        var r = await Run();

        Assert.Equal(SyncFailureKind.Rejected, r.Failure!.Kind);
        Assert.Contains("Anahtar geçersiz", r.Failure.Message);
        Assert.Equal(1, _outbox.CountPendingCuts());
    }

    [Fact]
    public async Task Html_login_page_is_reported_as_publish_problem()
    {
        _cuts.CreateCut("LOT-A", 100);
        _sheet.ForceStatus = System.Net.HttpStatusCode.OK;
        _sheet.ForceBody = "<!DOCTYPE html><html><body>Google Hesabınızla oturum açın</body></html>";

        var r = await Run();

        Assert.Equal(SyncFailureKind.Rejected, r.Failure!.Kind);
        Assert.Contains("Herkes", r.Failure.Message);
    }

    [Fact]
    public async Task Server_error_is_treated_as_temporary()
    {
        _cuts.CreateCut("LOT-A", 100);
        _sheet.ForceStatus = System.Net.HttpStatusCode.ServiceUnavailable;

        Assert.Equal(SyncFailureKind.Offline, (await Run()).Failure!.Kind);
    }

    [Fact]
    public async Task Test_connection_sends_empty_batch()
    {
        await _sync.TestConnectionAsync(new SyncTarget(Url, FakeAppsScript.Key));
        Assert.Equal([0], _sheet.BatchSizes);

        await Assert.ThrowsAsync<SyncException>(() => _sync.TestConnectionAsync(new SyncTarget(Url, "yanlis")));
    }

    [Theory]
    [InlineData("http://script.google.com/x")]
    [InlineData("script.google.com/x")]
    [InlineData("ftp://x")]
    public void Settings_reject_non_https_url(string url)
    {
        Assert.Throws<KumasBarkod.Core.Domain.CutValidationException>(() => _settings.SetSyncTarget(url, "k"));
    }

    [Fact]
    public void Empty_url_or_key_disables_sync()
    {
        _settings.SetSyncTarget("", "anahtar");
        Assert.Null(_settings.GetSyncTarget());
        _settings.SetSyncTarget(Url, "  ");
        Assert.Null(_settings.GetSyncTarget());
        _settings.SetSyncTarget(Url, "k");
        Assert.Equal(new SyncTarget(Url, "k"), _settings.GetSyncTarget());
    }

    // ---------- Temizlik ----------

    [Fact]
    public async Task Sent_rows_older_than_30_days_are_purged()
    {
        _cuts.CreateCut("LOT-A", 100);
        await Run();

        _clock.Now = _clock.Now.AddDays(31);
        _cuts.CreateCut("LOT-A", 100);
        await Run();

        using var c = _db.Factory.Open();
        Assert.Equal(["K1-000002"], c.Query<string>("SELECT cut_id FROM sync_outbox"));
        Assert.Equal(2L, c.ExecuteScalar<long>("SELECT count(*) FROM cuts"));   // kesimlere dokunulmaz
    }

    // ---------- Bekleme süreleri ----------

    [Theory]
    [InlineData(0, 120)]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 300)]
    [InlineData(5, 600)]
    [InlineData(50, 600)]
    public void Backoff_grows_and_caps_at_ten_minutes(int failures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), SyncWorker.Backoff(failures));
    }

    [Fact]
    public async Task Worker_sends_on_start_and_reports_idle()
    {
        _cuts.CreateCut("LOT-A", 100);
        var done = new TaskCompletionSource<SyncStatus>();
        await using var worker = new SyncWorker(_sync, _outbox, _clock);
        worker.StatusChanged += s => { if (s.State == SyncState.Idle) done.TrySetResult(s); };

        worker.Start();
        var status = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, status.PendingCount);
        Assert.NotNull(status.LastSuccessUtc);
        Assert.Single(_sheet.Sheet);
    }

    [Fact]
    public async Task Worker_reports_offline_with_next_attempt()
    {
        _cuts.CreateCut("LOT-A", 100);
        _sheet.FailNextAsOffline = 1;
        var done = new TaskCompletionSource<SyncStatus>();
        await using var worker = new SyncWorker(_sync, _outbox, _clock);
        worker.StatusChanged += s => { if (s.State == SyncState.Offline) done.TrySetResult(s); };

        worker.Start();
        var status = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, status.PendingCount);
        Assert.Equal(_clock.Now + TimeSpan.FromSeconds(30), status.NextAttemptUtc);
    }

    private sealed class NullPrinter : ILabelPrinter
    {
        public string DisplayName => "yok";
        public bool IsPhysical => false;
        public void Print(LabelData label) { }
    }

    private sealed class SettableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
