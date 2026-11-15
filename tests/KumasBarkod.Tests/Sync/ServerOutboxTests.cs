using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Services;
using KumasBarkod.Core.Sync;
using KumasBarkod.Tests.Data;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Tests.Sync;

/// <summary>İstasyon tarafı: depo sunucusu kuyruğu (şema v2), çift kuyruk yazımı, sunucu göndericisi.</summary>
public class ServerOutboxTests : IDisposable
{
    private static readonly Uri Server = new("http://depo-sunucu:5080/");

    private readonly TempDatabase _db = new();
    private readonly SettableClock _clock = new();
    private readonly SettingsRepository _settings;
    private readonly CutService _cuts;
    private readonly OutboxRepository _serverOutbox;
    private readonly FakeServer _server = new();
    private readonly ServerSyncService _sync;

    public ServerOutboxTests()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        _settings = new SettingsRepository(_db.Factory);
        _cuts = new CutService(_db.Factory, _settings, _clock);
        _serverOutbox = new OutboxRepository(_db.Factory, OutboxTables.Server);
        _sync = new ServerSyncService(_serverOutbox, _settings, new StationCounters(_db.Factory), _server,
            new StationIdentity("PC-1", "1.1.0"), _clock);
    }

    public void Dispose() => _db.Dispose();

    private long Rows(string table, string cutId)
    {
        using var c = _db.Factory.Open();
        return c.ExecuteScalar<long>($"SELECT count(*) FROM {table} WHERE cut_id = @cutId", new { cutId });
    }

    // ---------- Şema v2 ----------

    [Fact]
    public void Upgrade_from_v1_queues_existing_cuts_for_server_in_creation_order()
    {
        using var other = new TempDatabase();
        DatabaseMigrator.Migrate(other.Factory, Migrations.All.Take(1).ToList());
        using (var c = other.Factory.Open())
        {
            // Eski sürümle açılmış kesimler (biri iptal), kayıt sırası ID sırasından farklı
            c.Execute("""
                INSERT INTO cuts (cut_id, station_code, sequence_no, lot_code, length_cm, status, created_at)
                VALUES ('K1-000002', 'K1', 2, 'LOT-A', 100, 'active', '2026-11-01T09:00:00.000Z'),
                       ('K1-000001', 'K1', 1, 'LOT-A', 200, 'active', '2026-11-01T10:00:00.000Z');
                INSERT INTO cuts (cut_id, station_code, sequence_no, lot_code, length_cm, status, void_reason, voided_at, created_at)
                VALUES ('K2-000001', 'K2', 1, 'LOT-B', 300, 'voided', 'Çift kayıt', '2026-11-01T11:30:00.000Z', '2026-11-01T11:00:00.000Z');
                INSERT INTO sync_outbox (cut_id, operation, created_at, sent_at)
                VALUES ('K1-000001', 'upsert', '2026-11-01T10:00:00.000Z', '2026-11-01T10:01:00.000Z');
                """);
        }

        Assert.Equal(1, DatabaseMigrator.Migrate(other.Factory));

        using var check = other.Factory.Open();
        Assert.Equal(["K1-000002", "K1-000001", "K2-000001"],
            check.Query<string>("SELECT cut_id FROM server_outbox ORDER BY id"));
        Assert.Equal(3L, check.ExecuteScalar<long>("SELECT count(*) FROM server_outbox WHERE sent_at IS NULL"));
        // Kuyruk zamanı uygulamanın biçiminde (sabit uzunluk, Z)
        var created = check.ExecuteScalar<string>("SELECT created_at FROM server_outbox LIMIT 1")!;
        Timestamps.Parse(created);
        // Sheets kuyruğuna dokunulmadı
        Assert.Equal(1L, check.ExecuteScalar<long>("SELECT count(*) FROM sync_outbox"));
    }

    [Fact]
    public void Server_outbox_rejects_unknown_cut()
    {
        using var c = _db.Factory.Open();
        Assert.Throws<SqliteException>(() =>
            c.Execute("INSERT INTO server_outbox (cut_id, operation, created_at) VALUES ('K1-999999', 'upsert', '2026-11-01T10:00:00.000Z')"));
    }

    // ---------- İki kuyruğa birlikte yazım ----------

    [Fact]
    public void Create_print_and_void_are_queued_for_both_sheets_and_server()
    {
        var cut = _cuts.CreateCut("LOT-A", 2550);
        Assert.Equal(1, Rows(OutboxTables.Sheets, cut.CutId));
        Assert.Equal(1, Rows(OutboxTables.Server, cut.CutId));

        new PrintService(_db.Factory, new NullPrinter(), _clock).Print(cut.CutId);
        Assert.Equal(2, Rows(OutboxTables.Sheets, cut.CutId));
        Assert.Equal(2, Rows(OutboxTables.Server, cut.CutId));

        _cuts.VoidCut(cut.CutId, VoidReasons.All[0]);
        Assert.Equal(3, Rows(OutboxTables.Sheets, cut.CutId));
        Assert.Equal(3, Rows(OutboxTables.Server, cut.CutId));
    }

    [Fact]
    public void Cut_is_not_saved_when_server_queue_cannot_be_written()
    {
        using (var c = _db.Factory.Open())
            c.Execute("ALTER TABLE server_outbox RENAME TO server_outbox_broken");

        Assert.Throws<SqliteException>(() => _cuts.CreateCut("LOT-A", 100));

        using (var c = _db.Factory.Open())
        {
            Assert.Equal(0L, c.ExecuteScalar<long>("SELECT count(*) FROM cuts"));
            Assert.Equal(0L, c.ExecuteScalar<long>("SELECT count(*) FROM sync_outbox"));
            Assert.Equal(0L, c.ExecuteScalar<long>("SELECT coalesce(max(last_sequence), 0) FROM station_counter"));
        }
    }

    [Fact]
    public void Queues_are_independent()
    {
        var cut = _cuts.CreateCut("LOT-A", 100);
        var sheets = new OutboxRepository(_db.Factory);
        sheets.MarkSent(sheets.GetPendingBatch(10), _clock.GetUtcNow());

        Assert.Equal(0, sheets.CountPendingCuts());
        Assert.Equal(1, _serverOutbox.CountPendingCuts());
        Assert.Equal(cut.CutId, _serverOutbox.GetPendingBatch(10).Single().Row.CutId);
    }

    [Fact]
    public void Outbox_table_name_is_restricted()
    {
        Assert.Throws<ArgumentException>(() => new OutboxRepository(_db.Factory, "cuts; DROP TABLE cuts"));
    }

    // ---------- Sunucu adresi ----------

    [Theory]
    [InlineData("depo-sunucu", "http://depo-sunucu:5080/")]
    [InlineData("  192.168.1.20  ", "http://192.168.1.20:5080/")]
    [InlineData("192.168.1.20:6000", "http://192.168.1.20:6000/")]
    [InlineData("http://depo-sunucu", "http://depo-sunucu/")]
    [InlineData("https://depo.firma.local:8443/kumas", "https://depo.firma.local:8443/kumas/")]
    public void Server_address_is_normalized(string input, string expected)
    {
        Assert.True(ServerAddress.TryNormalize(input, out var uri, out var error), error);
        Assert.Equal(expected, uri!.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://depo")]
    [InlineData("http://")]
    [InlineData("depo-sunucu/?x=1")]
    public void Invalid_server_address_is_rejected(string input)
    {
        Assert.False(ServerAddress.TryNormalize(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Server_url_setting_round_trips_and_clears()
    {
        Assert.Null(_settings.GetServerBaseUri());
        Assert.Equal(Server, _settings.SetServerUrl("depo-sunucu"));
        Assert.Equal(Server, _settings.GetServerBaseUri());
        Assert.Throws<CutValidationException>(() => _settings.SetServerUrl("ftp://x"));
        Assert.Equal(Server, _settings.GetServerBaseUri());   // geçersiz giriş eskisini bozmaz
        Assert.Null(_settings.SetServerUrl(""));
        Assert.Null(_settings.GetServerBaseUri());
    }

    // ---------- Sunucu göndericisi ----------

    [Fact]
    public async Task Not_configured_sends_nothing_and_keeps_queue()
    {
        _cuts.CreateCut("LOT-A", 100);

        var r = await _sync.RunOnceAsync();

        Assert.False(r.Configured);
        Assert.Empty(_server.Requests);
        Assert.Equal(1, _serverOutbox.CountPendingCuts());
    }

    [Fact]
    public async Task First_run_handshakes_then_sends_current_state_once()
    {
        _settings.SetServerUrl("depo-sunucu");
        var cut = _cuts.CreateCut("LOT-A", 2550);
        _cuts.VoidCut(cut.CutId, VoidReasons.All[1]);

        var r = await _sync.RunOnceAsync();

        Assert.True(r.Succeeded);
        Assert.Equal(1, r.SentCuts);
        Assert.Equal(2, _server.Requests.Count);
        Assert.Empty(_server.Requests[0].Rows);                 // el sıkışma: veri yok
        Assert.Equal("PC-1", _server.Requests[0].InstallId);
        Assert.Equal("K1", _server.Requests[0].StationCode);
        var row = Assert.Single(_server.Requests[1].Rows);       // güncel hâl tek satır
        Assert.Equal(CutStatus.Voided, row.Status);
        Assert.Equal(0, _serverOutbox.CountPendingCuts());

        // İkinci tur: el sıkışma tekrar yok, gönderilecek bir şey yok
        await _sync.RunOnceAsync();
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task Handshake_raises_local_counter_so_ids_are_not_reused()
    {
        _settings.SetServerUrl("depo-sunucu");
        _server.MaxSequences["K1"] = 41;

        await _sync.RunOnceAsync();

        Assert.Equal("K1-000042", _cuts.CreateCut("LOT-A", 100).CutId);
    }

    [Fact]
    public async Task Offline_server_keeps_queue_and_counts_attempts()
    {
        _settings.SetServerUrl("depo-sunucu");
        _cuts.CreateCut("LOT-A", 100);
        _server.Fail = new SyncException(SyncFailureKind.Offline, "Depo sunucusuna ulaşılamıyor");

        var r = await _sync.RunOnceAsync();

        Assert.Equal(SyncFailureKind.Offline, r.Failure!.Kind);
        Assert.Equal(1, _serverOutbox.CountPendingCuts());
        using (var c = _db.Factory.Open())
            Assert.Equal(1L, c.ExecuteScalar<long>("SELECT attempts FROM server_outbox"));

        _server.Fail = null;
        Assert.True((await _sync.RunOnceAsync()).Succeeded);
        Assert.Equal(0, _serverOutbox.CountPendingCuts());
    }

    [Fact]
    public async Task Change_during_send_is_not_lost()
    {
        _settings.SetServerUrl("depo-sunucu");
        var cut = _cuts.CreateCut("LOT-A", 100);
        await _sync.RunOnceAsync();                      // el sıkışma + ilk gönderim
        _server.Requests.Clear();

        var printer = new PrintService(_db.Factory, new NullPrinter(), _clock);
        var other = _cuts.CreateCut("LOT-A", 200);       // gönderilecek değişiklik
        _server.OnSend = () => printer.Print(cut.CutId); // paket yoldayken ilk kesimin etiketi basılıyor

        await _sync.RunOnceAsync();

        // Yoldaki paket kapandı, baskı aynı turun sonraki paketinde gitti
        Assert.Equal(0, _serverOutbox.CountPendingCuts());
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(other.CutId, _server.Requests[0].Rows.Single().CutId);
        var printed = _server.Requests[1].Rows.Single();
        Assert.Equal(cut.CutId, printed.CutId);
        Assert.Equal(1, printed.PrintCount);
    }

    [Fact]
    public async Task Conflicted_cut_stays_queued_and_reports_error()
    {
        _settings.SetServerUrl("depo-sunucu");
        var a = _cuts.CreateCut("LOT-A", 100);
        var b = _cuts.CreateCut("LOT-A", 200);
        _server.ConflictIds.Add(a.CutId);

        var r = await _sync.RunOnceAsync();

        Assert.Equal(SyncFailureKind.Rejected, r.Failure!.Kind);
        Assert.Contains(a.CutId, r.Failure.Message);
        Assert.Contains("karantina", r.Failure.Message);
        Assert.Equal(1, r.SentCuts);
        Assert.Equal(1, _serverOutbox.CountPendingCuts());
        Assert.Equal(a.CutId, _serverOutbox.GetPendingBatch(10).Single().Row.CutId);
        Assert.Equal(0, Rows(OutboxTables.Server, b.CutId) - SentRows(b.CutId));
    }

    private long SentRows(string cutId)
    {
        using var c = _db.Factory.Open();
        return c.ExecuteScalar<long>("SELECT count(*) FROM server_outbox WHERE cut_id = @cutId AND sent_at IS NOT NULL", new { cutId });
    }

    [Fact]
    public async Task Rejection_repeats_handshake_next_time()
    {
        _settings.SetServerUrl("depo-sunucu");
        await _sync.RunOnceAsync();
        _cuts.CreateCut("LOT-A", 100);
        _server.Fail = new SyncException(SyncFailureKind.Rejected, "K1 başka bilgisayara kayıtlı");
        await _sync.RunOnceAsync();
        _server.Fail = null;
        _server.Requests.Clear();

        await _sync.RunOnceAsync();

        Assert.Empty(_server.Requests[0].Rows);   // önce yeniden el sıkışma
        Assert.Single(_server.Requests[1].Rows);
    }

    [Fact]
    public async Task Station_code_change_repeats_handshake_with_new_code()
    {
        _settings.SetServerUrl("depo-sunucu");
        await _sync.RunOnceAsync();
        _settings.SetStationCode("K2");
        _server.Requests.Clear();

        await _sync.RunOnceAsync();

        var hello = Assert.Single(_server.Requests);
        Assert.Equal("K2", hello.StationCode);
        Assert.Empty(hello.Rows);
    }

    [Theory]
    [InlineData(StationRoles.Dock)]
    [InlineData(StationRoles.Office)]
    public async Task Non_qc_computer_does_not_register_as_station(string role)
    {
        _settings.SetServerUrl("depo-sunucu");
        _settings.SetStationRole(role);

        var r = await _sync.RunOnceAsync();

        Assert.True(r.Succeeded);
        Assert.Empty(_server.Requests);   // varsayılan K1 kodunu gerçek QC bilgisayarından önce kapmasın
    }

    [Fact]
    public async Task Computer_whose_role_changed_still_sends_its_pending_cuts()
    {
        _settings.SetServerUrl("depo-sunucu");
        _cuts.CreateCut("LOT-A", 100);
        _settings.SetStationRole(StationRoles.Office);

        await _sync.RunOnceAsync();

        Assert.Single(_server.Requests[1].Rows);
        Assert.Equal(0, _serverOutbox.CountPendingCuts());
    }

    [Fact]
    public void Station_role_defaults_to_qc_and_validates()
    {
        Assert.Equal(StationRoles.Qc, _settings.GetStationRole());
        _settings.SetStationRole(StationRoles.Dock);
        Assert.Equal(StationRoles.Dock, _settings.GetStationRole());
        Assert.Throws<CutValidationException>(() => _settings.SetStationRole("patron"));
    }

    [Fact]
    public async Task Large_queue_is_sent_in_batches_of_200()
    {
        _settings.SetServerUrl("depo-sunucu");
        for (var i = 0; i < 450; i++) _cuts.CreateCut("LOT-A", 100 + i);

        var r = await _sync.RunOnceAsync();

        Assert.Equal(450, r.SentCuts);
        Assert.Equal([0, 200, 200, 50], _server.Requests.Select(x => x.Rows.Count));
    }

    [Fact]
    public async Task Worker_uses_short_interval_and_capped_backoff_for_server()
    {
        await using var worker = new SyncWorker(_sync, _serverOutbox, _clock,
            normalInterval: TimeSpan.FromSeconds(30), nudgeDelay: TimeSpan.FromSeconds(1), maxBackoff: TimeSpan.FromMinutes(1));

        Assert.Equal(TimeSpan.FromSeconds(30), worker.Wait(0));
        Assert.Equal(TimeSpan.FromSeconds(30), worker.Wait(1));
        Assert.Equal(TimeSpan.FromMinutes(1), worker.Wait(2));
        Assert.Equal(TimeSpan.FromMinutes(1), worker.Wait(5));   // Sheets'te 10 dk olurdu

        // Sheets göndericisi değişmedi
        await using var sheets = new SyncWorker(_sync, _serverOutbox, _clock);
        Assert.Equal(SyncWorker.NormalInterval, sheets.Wait(0));
        Assert.Equal(TimeSpan.FromMinutes(10), sheets.Wait(5));
    }

    [Fact]
    public async Task Worker_reports_offline_with_pending_count()
    {
        _settings.SetServerUrl("depo-sunucu");
        _server.Fail = new SyncException(SyncFailureKind.Offline, "yok");
        for (var i = 0; i < 5; i++) _cuts.CreateCut("LOT-A", 100);
        await using var worker = new SyncWorker(_sync, _serverOutbox, _clock, maxBackoff: TimeSpan.FromMinutes(1));
        var done = new TaskCompletionSource<SyncStatus>();
        worker.StatusChanged += s => { if (s.State == SyncState.Offline) done.TrySetResult(s); };

        worker.Start();
        var status = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(5, status.PendingCount);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromSeconds(30), status.NextAttemptUtc);
    }

    // ---------- Yardımcılar ----------

    private sealed class FakeServer : IServerTransport
    {
        public List<CutsBatchRequest> Requests { get; } = [];
        public Dictionary<string, long> MaxSequences { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ConflictIds { get; } = new(StringComparer.Ordinal);
        public SyncException? Fail { get; set; }
        public Action? OnSend { get; set; }

        public Task<CutsBatchResponse> SendCutsAsync(Uri baseUri, CutsBatchRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal(Server, baseUri);
            if (Fail is not null) throw Fail;
            Requests.Add(request);
            var once = OnSend;
            OnSend = null;
            once?.Invoke();
            var conflicts = request.Rows.Where(r => ConflictIds.Contains(r.CutId))
                .Select(r => new ServerCutConflict(r.CutId, r.CreatedAt, "2020-01-01T00:00:00.000Z")).ToList();
            return Task.FromResult(new CutsBatchResponse(true, ServerApiContract.ApiVersion,
                request.Rows.Count - conflicts.Count, conflicts, MaxSequences));
        }

        public Task<HealthResponse> HealthAsync(Uri baseUri, CancellationToken cancellationToken) =>
            Task.FromResult(new HealthResponse(true, "KumasBarkod.Server", 1, "0.1.0", 1, "2026-11-01T09:00:00.000Z"));
    }

    private sealed class NullPrinter : ILabelPrinter
    {
        public string DisplayName => "Test";
        public bool IsPhysical => false;
        public void Print(LabelData label) { }
    }

    private sealed class SettableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
