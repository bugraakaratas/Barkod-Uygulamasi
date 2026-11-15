using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Services;
using KumasBarkod.Core.Sync;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Server.Tests;

/// <summary>
/// Uçtan uca: istasyonun GERÇEK veritabanı + kesim/baskı/iptal servisleri + gerçek HTTP göndericisi →
/// gerçek sunucu (bellek içi HTTP) → sunucu veritabanı. WPF'in kullandığı kodun aynısı.
/// </summary>
public sealed class StationToServerTests : IDisposable
{
    private static readonly Uri BaseUri = new("http://localhost/");

    private readonly ServerFixture _server = new();
    private readonly List<Station> _stations = [];

    public void Dispose()
    {
        Assert.Empty(_server.Invariants());
        foreach (var s in _stations) s.Dispose();
        _server.Dispose();
    }

    private Station NewStation(string installId, string code = "K1")
    {
        var s = new Station(_server, installId, code);
        _stations.Add(s);
        return s;
    }

    private long ServerCount(string sql)
    {
        using var c = _server.Open();
        return c.ExecuteScalar<long>(sql);
    }

    [Fact]
    public async Task Created_printed_and_voided_cuts_reach_server_stock()
    {
        var k1 = NewStation("PC-1");
        var a = k1.CreatePrinted("LOT-3", 2550);
        var b = k1.CreatePrinted("LOT-3", 1800);
        var c = k1.CreatePrinted("LOT-5", 900);
        k1.Cuts.VoidCut(c.CutId, VoidReasons.All[0]);

        var r = await k1.Sync.RunOnceAsync();

        Assert.True(r.Succeeded, r.Failure?.Message);
        Assert.Equal(3, r.SentCuts);
        Assert.Equal(0, k1.Outbox.CountPendingCuts());
        using var db = _server.Open();
        var stock = db.Query<(string CutId, string State, long Cm)>(
            "SELECT cut_id, state, length_cm FROM rolls ORDER BY cut_id").AsList();
        Assert.Equal([(a.CutId, "in_stock", 2550L), (b.CutId, "in_stock", 1800L), (c.CutId, "voided", 900L)], stock);
        // Stok: yalnızca 'in_stock' sayılır
        Assert.Equal(4350L, db.ExecuteScalar<long>("SELECT sum(length_cm) FROM rolls WHERE state = 'in_stock'"));
        Assert.Equal(1L, db.ExecuteScalar<long>("SELECT print_count FROM rolls WHERE cut_id = @id", new { id = a.CutId }));
    }

    [Fact]
    public async Task Cuts_made_while_server_was_down_arrive_later_exactly_once()
    {
        var k1 = NewStation("PC-1");
        k1.Settings.SetServerUrl("http://127.0.0.1:1");   // kapalı sunucu
        var offline = new ServerSyncService(k1.Outbox, k1.Settings, new StationCounters(k1.Db), new HttpServerTransport(
            new HttpClient { Timeout = TimeSpan.FromSeconds(2) }), new StationIdentity("PC-1", "1.1.0"), TimeProvider.System);
        for (var i = 0; i < 5; i++) k1.CreatePrinted("LOT-3", 100 + i);

        var down = await offline.RunOnceAsync();
        Assert.Equal(SyncFailureKind.Offline, down.Failure!.Kind);
        // Windows kapalı kapıda bağlantıyı bazen hemen reddeder, bazen zaman aşımına düşer: ikisi de "çevrimdışı"
        Assert.Matches("ulaşılamıyor|yanıt vermedi", down.Failure.Message);
        Assert.Equal(5, k1.Outbox.CountPendingCuts());

        k1.Settings.SetServerUrl(BaseUri.ToString());
        Assert.True((await k1.Sync.RunOnceAsync()).Succeeded);
        Assert.True((await k1.Sync.RunOnceAsync()).Succeeded);

        Assert.Equal(5, ServerCount("SELECT count(*) FROM rolls"));
        Assert.Equal(5, ServerCount("SELECT count(*) FROM stock_movements"));
    }

    [Fact]
    public async Task Lost_response_and_resend_does_not_duplicate()
    {
        var k1 = NewStation("PC-1");
        k1.CreatePrinted("LOT-3", 100);
        await k1.Sync.RunOnceAsync();

        // Yanıt kayboldu gibi: kuyruğu yeniden "gönderilmemiş" yap, tekrar gönder
        using (var c = k1.Db.Open())
            c.Execute("UPDATE server_outbox SET sent_at = NULL");
        Assert.True((await k1.Sync.RunOnceAsync()).Succeeded);

        Assert.Equal(1, ServerCount("SELECT count(*) FROM rolls"));
        Assert.Equal(1, ServerCount("SELECT count(*) FROM stock_movements"));
    }

    [Fact]
    public async Task Second_computer_with_same_station_code_is_stopped_and_keeps_its_cuts()
    {
        var pc1 = NewStation("PC-1");
        pc1.CreatePrinted("LOT-3", 100);
        await pc1.Sync.RunOnceAsync();

        var pc2 = NewStation("PC-2");             // yanlışlıkla aynı K1 kodu
        pc2.CreatePrinted("LOT-9", 999);

        var r = await pc2.Sync.RunOnceAsync();

        Assert.Equal(SyncFailureKind.Rejected, r.Failure!.Kind);
        Assert.Contains("başka bir bilgisayara kayıtlı", r.Failure.Message);
        Assert.Equal(1, pc2.Outbox.CountPendingCuts());                    // kayıt kaybolmadı
        Assert.Equal(1, ServerCount("SELECT count(*) FROM rolls"));       // PC-1'in topu ezilmedi
        using (var c = _server.Open())
            Assert.Equal("LOT-3", c.ExecuteScalar<string>("SELECT lot_code_raw FROM rolls"));

        // Çözüm: PC-2'ye farklı kod verilir. Yeni kesimler gider; eski K1 kesimi de sunucuya ulaşır ama numarası
        // PC-1'in topuyla aynı olduğu için iki top KARANTİNAYA alınır (aynı barkodlu iki top sevk edilemez).
        pc2.Settings.SetStationCode("K2");
        var newCut = pc2.CreatePrinted("LOT-9", 500);
        Assert.Equal("K2-000001", newCut.CutId);
        var again = await pc2.Sync.RunOnceAsync();

        Assert.Equal(SyncFailureKind.Rejected, again.Failure!.Kind);
        Assert.Contains("karantina", again.Failure.Message);
        Assert.Equal(1, pc2.Outbox.CountPendingCuts());                    // çakışan kesim uyarı için kuyrukta
        using var db = _server.Open();
        Assert.Equal([("K1-000001", "quarantined"), ("K2-000001", "in_stock")],
            db.Query<(string, string)>("SELECT cut_id, state FROM rolls ORDER BY cut_id"));
        Assert.Equal(1L, db.ExecuteScalar<long>("SELECT count(*) FROM cut_conflicts"));
    }

    [Fact]
    public async Task Reinstalled_station_learns_counter_from_server_and_does_not_reuse_ids()
    {
        var old = NewStation("PC-1");
        for (var i = 0; i < 3; i++) old.CreatePrinted("LOT-3", 100);
        await old.Sync.RunOnceAsync();

        // Aynı bilgisayar, veritabanı silinip yeniden kuruldu (Sheets'e de bağlı değil)
        var fresh = NewStation("PC-1");
        await fresh.Sync.RunOnceAsync();                         // el sıkışma: sayaç öğrenilir
        var next = fresh.CreatePrinted("LOT-3", 100);
        Assert.Equal("K1-000004", next.CutId);

        Assert.True((await fresh.Sync.RunOnceAsync()).Succeeded);
        Assert.Equal(4, ServerCount("SELECT count(*) FROM rolls"));
        Assert.Equal(0, ServerCount("SELECT count(*) FROM cut_conflicts"));
    }

    [Fact]
    public async Task Fresh_database_that_reused_ids_before_connecting_is_quarantined()
    {
        var old = NewStation("PC-1");
        old.CreatePrinted("LOT-3", 2550);
        await old.Sync.RunOnceAsync();

        // Sunucuya bağlanmadan önce kesim açılmış yeni veritabanı: K1-000001 iki ayrı topa verildi
        var fresh = NewStation("PC-1");
        fresh.Settings.SetServerUrl("");
        var dup = fresh.CreatePrinted("LOT-7", 900);
        Assert.Equal("K1-000001", dup.CutId);
        fresh.Settings.SetServerUrl(BaseUri.ToString());

        var r = await fresh.Sync.RunOnceAsync();

        Assert.Equal(SyncFailureKind.Rejected, r.Failure!.Kind);
        Assert.Contains("karantina", r.Failure.Message);
        Assert.Equal(1, fresh.Outbox.CountPendingCuts());
        using var c = _server.Open();
        Assert.Equal(("quarantined", "LOT-3"), c.QuerySingle<(string, string)>("SELECT state, lot_code_raw FROM rolls"));
        Assert.Equal(1L, c.ExecuteScalar<long>("SELECT count(*) FROM cut_conflicts"));
        // Sonraki kesim artık çakışmaz (sayaç öğrenildi)
        Assert.Equal("K1-000002", fresh.CreatePrinted("LOT-7", 900).CutId);
    }

    [Fact]
    public async Task Health_check_through_real_transport()
    {
        var k1 = NewStation("PC-1");
        var health = await k1.Sync.TestConnectionAsync(BaseUri);
        Assert.Equal("KumasBarkod.Server", health.Service);

        var wrong = new HttpServerTransport(_server.CreateClient());
        var ex = await Assert.ThrowsAsync<SyncException>(() => wrong.HealthAsync(new Uri("http://localhost/yok/"), default));
        Assert.Equal(SyncFailureKind.Rejected, ex.Kind);
    }

    [Fact]
    public async Task Two_stations_in_parallel_stay_consistent()
    {
        var k1 = NewStation("PC-1", "K1");
        var k2 = NewStation("PC-2", "K2");
        var work = new[] { k1, k2 }.Select(s => Task.Run(async () =>
        {
            for (var i = 0; i < 30; i++)
            {
                var cut = s.CreatePrinted("LOT-3", 100 + i);
                if (i % 7 == 0) s.Cuts.VoidCut(cut.CutId, VoidReasons.All[2]);
                if (i % 5 == 0) Assert.True((await s.Sync.RunOnceAsync()).Succeeded);
            }
            Assert.True((await s.Sync.RunOnceAsync()).Succeeded);
        }));
        await Task.WhenAll(work);

        Assert.Equal(60, ServerCount("SELECT count(*) FROM rolls"));
        Assert.Equal(2 * 5, ServerCount("SELECT count(*) FROM rolls WHERE state = 'voided'"));
        Assert.Equal(60 + 10, ServerCount("SELECT count(*) FROM stock_movements"));
        Assert.Equal(0, k1.Outbox.CountPendingCuts() + k2.Outbox.CountPendingCuts());
    }

    /// <summary>Bir QC bilgisayarı: kendi geçici veritabanı + uygulamanın servisleri.</summary>
    private sealed class Station : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "kumas-station-tests", Guid.NewGuid().ToString("N"));

        public Station(ServerFixture server, string installId, string code)
        {
            Db = new SqliteConnectionFactory(Path.Combine(_dir, "kumas.db"));
            DatabaseMigrator.Migrate(Db);
            Settings = new SettingsRepository(Db);
            Settings.SetStationCode(code);
            Settings.SetServerUrl(BaseUri.ToString());
            Cuts = new CutService(Db, Settings, TimeProvider.System);
            Printer = new PrintService(Db, new NullPrinter(), TimeProvider.System);
            Outbox = new OutboxRepository(Db, OutboxTables.Server);
            Sync = new ServerSyncService(Outbox, Settings, new StationCounters(Db),
                new HttpServerTransport(server.CreateClient()), new StationIdentity(installId, "1.1.0"), TimeProvider.System);
        }

        public SqliteConnectionFactory Db { get; }
        public SettingsRepository Settings { get; }
        public CutService Cuts { get; }
        public PrintService Printer { get; }
        public OutboxRepository Outbox { get; }
        public ServerSyncService Sync { get; }

        public Cut CreatePrinted(string lot, long cm)
        {
            var cut = Cuts.CreateCut(lot, cm);
            return Printer.Print(cut.CutId);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class NullPrinter : ILabelPrinter
    {
        public string DisplayName => "Test";
        public bool IsPhysical => false;
        public void Print(LabelData label) { }
    }
}
