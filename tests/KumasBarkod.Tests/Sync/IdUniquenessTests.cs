using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Services;
using KumasBarkod.Core.Sync;
using KumasBarkod.Tests.Data;

namespace KumasBarkod.Tests.Sync;

/// <summary>
/// Aynı Google tablosuna yazan birden çok veritabanı (yeniden kurulum, test veritabanı, aynı
/// istasyon kodlu ikinci bilgisayar). Kural: tablodaki her ID tek bir kesime aittir, hiçbir kesim
/// başkasının üzerine yazılmaz ve satırlar kayıt zamanına göre sıralıdır (en yeni en altta).
/// </summary>
public class IdUniquenessTests : IDisposable
{
    private const string Url = "https://script.google.com/macros/s/TEST/exec";

    private readonly FakeAppsScript _sheet = new();
    private readonly Clock _clock = new();
    private readonly List<TempDatabase> _dbs = [];

    public void Dispose() => _dbs.ForEach(d => d.Dispose());

    /// <summary>Tabloya bağlı ayrı bir kurulum (kendi veritabanı).</summary>
    private sealed record Install(CutService Cuts, SyncService Sync, OutboxRepository Outbox, StationCounters Counters, SettingsRepository Settings);

    private Install NewInstall(string station = "K1", bool configured = true)
    {
        var db = new TempDatabase();
        _dbs.Add(db);
        DatabaseMigrator.Migrate(db.Factory);
        var settings = new SettingsRepository(db.Factory);
        settings.SetStationCode(station);
        if (configured) settings.SetSyncTarget(Url, FakeAppsScript.Key);
        var outbox = new OutboxRepository(db.Factory);
        var counters = new StationCounters(db.Factory);
        var sync = new SyncService(outbox, settings, counters, new AppsScriptTransport(new HttpClient(_sheet)), _clock);
        return new Install(new CutService(db.Factory, settings, _clock), sync, outbox, counters, settings);
    }

    private string Cut(Install i, string lot)
    {
        _clock.Now = _clock.Now.AddSeconds(1);   // her kesim farklı zamanda
        return i.Cuts.CreateCut(lot, 1000).CutId;
    }

    private List<string> SheetIds() => _sheet.Sheet.Select(r => r["cut_id"].GetString()!).ToList();
    private string Lot(string cutId) => _sheet.Row(cutId)["lot_code"].GetString()!;

    private void AssertUniqueAndChronological()
    {
        var ids = SheetIds();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        var created = _sheet.Sheet.Select(r => r["created_at"].GetString()!).ToList();
        Assert.Equal(created.OrderBy(c => c, StringComparer.Ordinal), created);
    }

    /// <summary>
    /// Bildirilen hata: test veritabanı K1-000001..3 yazmıştı; gerçek uygulama (yeni veritabanı)
    /// ayarlanınca kendi K1-000001'ini yazıp eski satırı siliyordu. Artık sayaç tablodan öğrenilir.
    /// </summary>
    [Fact]
    public async Task New_install_continues_after_ids_already_in_sheet()
    {
        var test = NewInstall();
        Cut(test, "TEST-1"); Cut(test, "TEST-2"); Cut(test, "TEST-3");
        Assert.True((await test.Sync.RunOnceAsync()).Succeeded);

        var real = NewInstall();
        Assert.True((await real.Sync.RunOnceAsync()).Succeeded);   // bağlanınca sayaçları öğrenir
        Assert.Equal(3, real.Counters.GetLast("K1"));

        var id = Cut(real, "Lot 31");
        Assert.Equal("K1-000004", id);
        Assert.True((await real.Sync.RunOnceAsync()).Succeeded);

        Assert.Equal(["K1-000001", "K1-000002", "K1-000003", "K1-000004"], SheetIds());
        Assert.Equal("TEST-1", Lot("K1-000001"));                  // eski kayıt yerinde
        Assert.Equal("Lot 31", Lot("K1-000004"));                  // yeni kayıt en altta
        AssertUniqueAndChronological();
    }

    /// <summary>
    /// Ayarlanmadan (internetsiz) kesim yapılmış ve ID tabloda başka bir kesimle çakışıyor:
    /// tablodaki kayıt EZİLMEZ, çakışan kesim kuyrukta kalır, hata görünür, çakışmayanlar gider.
    /// </summary>
    [Fact]
    public async Task Conflicting_id_never_overwrites_sheet_row()
    {
        var first = NewInstall();
        Cut(first, "ESKI-1"); Cut(first, "ESKI-2");
        await first.Sync.RunOnceAsync();

        var second = NewInstall(configured: false);
        Cut(second, "YENI-1");                       // K1-000001 → çakışacak
        Cut(second, "YENI-2");                       // K1-000002 → çakışacak
        Cut(second, "YENI-3");                       // K1-000003 → tabloda yok
        second.Settings.SetSyncTarget(Url, FakeAppsScript.Key);

        var r = await second.Sync.RunOnceAsync();

        Assert.False(r.Succeeded);
        Assert.Equal(SyncFailureKind.Rejected, r.Failure!.Kind);
        Assert.Contains("K1-000001", r.Failure.Message);
        Assert.Contains("1 kayıt daha", r.Failure.Message);
        Assert.Contains("istasyon kodu", r.Failure.Message);
        Assert.Equal(1, r.SentCuts);

        Assert.Equal("ESKI-1", Lot("K1-000001"));
        Assert.Equal("ESKI-2", Lot("K1-000002"));
        Assert.Equal("YENI-3", Lot("K1-000003"));
        Assert.Equal(2, second.Outbox.CountPendingCuts());    // veri kaybolmadı, bekliyor
        AssertUniqueAndChronological();

        // Sayaç tablodan öğrenildi: sonraki kesim çakışmaz
        Assert.Equal("K1-000004", Cut(second, "YENI-4"));

        // Tekrar denemede çakışanlar yine reddedilir, yenisi gider
        var r2 = await second.Sync.RunOnceAsync();
        Assert.False(r2.Succeeded);
        Assert.Equal("YENI-4", Lot("K1-000004"));
        Assert.Equal("ESKI-1", Lot("K1-000001"));
        AssertUniqueAndChronological();
    }

    [Fact]
    public async Task Conflict_is_recorded_in_outbox_error()
    {
        var first = NewInstall();
        Cut(first, "ESKI");
        await first.Sync.RunOnceAsync();

        var second = NewInstall(configured: false);
        Cut(second, "YENI");
        second.Settings.SetSyncTarget(Url, FakeAppsScript.Key);
        await second.Sync.RunOnceAsync();

        Assert.Single(_sheet.Sheet);
        Assert.Equal("ESKI", Lot("K1-000001"));
        Assert.Equal(1, second.Outbox.CountPendingCuts());
    }

    /// <summary>Aynı kesimin tekrar gönderimi (baskı, iptal, kaybolan yanıt) hâlâ aynı satırı günceller.</summary>
    [Fact]
    public async Task Same_cut_still_updates_its_own_row()
    {
        var i = NewInstall();
        var id = Cut(i, "LOT");
        await i.Sync.RunOnceAsync();
        i.Cuts.VoidCut(id, "yanlış metre");

        Assert.True((await i.Sync.RunOnceAsync()).Succeeded);
        Assert.Single(_sheet.Sheet);
        Assert.Equal("voided", _sheet.Row(id)["status"].GetString());
    }

    /// <summary>
    /// Başka istasyonun internetsiz biriken eski kayıtları sonradan gelirse araya, kayıt zamanına
    /// göre yerleşir; tablo her zaman eskiden yeniye sıralıdır.
    /// </summary>
    [Fact]
    public async Task Rows_are_ordered_by_creation_time_across_stations()
    {
        var k1 = NewInstall("K1");
        var k2 = NewInstall("K2");

        Cut(k2, "K2-ESKI");          // en eski, ama internetsiz kaldı
        Cut(k1, "K1-A");
        Cut(k1, "K1-B");
        await k1.Sync.RunOnceAsync();
        Cut(k2, "K2-YENI");          // en yeni
        await k2.Sync.RunOnceAsync();

        Assert.Equal(["K2-000001", "K1-000001", "K1-000002", "K2-000002"], SheetIds());
        AssertUniqueAndChronological();
    }

    [Fact]
    public async Task Many_cuts_end_up_in_save_order_newest_last()
    {
        var i = NewInstall();
        var ids = new List<string>();
        for (var n = 0; n < 450; n++)
        {
            ids.Add(Cut(i, "LOT"));
            if (n % 97 == 0) await i.Sync.RunOnceAsync();
        }
        await i.Sync.RunOnceAsync();

        Assert.Equal(ids, SheetIds());
        Assert.Equal("K1-000450", SheetIds()[^1]);
        AssertUniqueAndChronological();
    }

    [Fact]
    public async Task Old_script_without_protection_is_refused_and_nothing_marked_sent()
    {
        _sheet.Version = 2;
        var i = NewInstall();
        Cut(i, "LOT");

        var r = await i.Sync.RunOnceAsync();

        Assert.Equal(SyncFailureKind.Rejected, r.Failure!.Kind);
        Assert.Contains("eski sürüm", r.Failure.Message);
        Assert.Equal(1, i.Outbox.CountPendingCuts());
    }

    [Fact]
    public async Task Counters_are_learned_only_once_per_sheet_address_when_idle()
    {
        _sheet.AddForeignRow("K1-000042", "2026-10-01T08:00:00.000Z");
        var i = NewInstall();

        await i.Sync.RunOnceAsync();
        await i.Sync.RunOnceAsync();

        Assert.Equal(1, _sheet.RequestCount);       // boşta her turda istek atılmaz
        Assert.Equal(42, i.Counters.GetLast("K1"));
        Assert.Equal("K1-000043", Cut(i, "LOT"));
    }

    [Fact]
    public void Counter_is_never_lowered_capped_and_ignores_bad_codes()
    {
        var i = NewInstall();
        Cut(i, "A"); Cut(i, "B"); Cut(i, "C");

        i.Counters.RaiseTo(new Dictionary<string, long> { ["K1"] = 2, ["K2"] = 5_000_000, ["k3"] = 7, ["XX1"] = 9 });

        Assert.Equal(3, i.Counters.GetLast("K1"));
        Assert.Equal(999_999, i.Counters.GetLast("K2"));
        Assert.Equal(0, i.Counters.GetLast("k3"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
