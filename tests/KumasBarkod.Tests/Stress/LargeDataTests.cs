using System.Diagnostics;
using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using KumasBarkod.Core.Services;
using KumasBarkod.Core.Sync;
using KumasBarkod.Tests.Data;
using KumasBarkod.Tests.Sync;
using Xunit.Abstractions;

namespace KumasBarkod.Tests.Stress;

/// <summary>
/// Büyük veri ölçümleri. Uzun sürdüğü için yalnızca KUMAS_STRESS=1 ile çalışır; ölçümler
/// KUMAS_STRESS_OUT dosyasına da yazılır. Sınırlar kullanıcının hissedeceği sürelerdir.
/// </summary>
[Collection(TimingCollection.Name)]
public class LargeDataTests(ITestOutputHelper output)
{
    private const string Url = "https://script.google.com/macros/s/TEST/exec";
    private readonly List<string> _overLimit = [];
    private static bool Enabled => Environment.GetEnvironmentVariable("KUMAS_STRESS") == "1";

    private void Report(string line)
    {
        output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("KUMAS_STRESS_OUT") is { Length: > 0 } path)
            File.AppendAllText(path, line + Environment.NewLine);
    }

    private double Measure(string name, double limitMs, Action action)
    {
        action();   // ilk çalıştırma: önbellek ısınır, ölçüme girmez
        var samples = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        var median = samples[2];
        Report($"{name,-44} orta {median,9:0.0} ms   en kötü {samples[^1],9:0.0} ms   sınır {limitMs:0} ms");
        if (median > limitMs) _overLimit.Add($"{name}: {median:0.0} ms > {limitMs} ms");
        return median;
    }

    [Fact]
    public void One_million_cuts_stay_usable()
    {
        if (!Enabled) return;

        using var db = new TempDatabase();
        DatabaseMigrator.Migrate(db.Factory);
        var settings = new SettingsRepository(db.Factory);
        var clock = TimeProvider.System;
        var cuts = new CutService(db.Factory, settings, clock);
        var repo = new CutRepository(db.Factory);
        var print = new PrintService(db.Factory, new NullPrinter(), clock, TimeZoneInfo.Utc);
        var outbox = new OutboxRepository(db.Factory);

        const int rows = 999_000;
        var sw = Stopwatch.StartNew();
        BulkData.Insert(db.Factory, rows, DateTime.UtcNow, TimeSpan.FromSeconds(100));
        Report($"{rows:N0} kayıt yükleme: {sw.Elapsed.TotalSeconds:0.0} sn, dosya {new FileInfo(db.Factory.DatabasePath).Length / 1024 / 1024} MB");

        var tz = TimeZoneInfo.Utc;
        Measure("Kayıt (CutService.CreateCut)", 100, () => cuts.CreateCut("LOT-0001", 2550));
        Measure("Son lotlar (10)", 50, () => repo.GetRecentLots());
        Measure("Son kayıtlar (12)", 50, () => repo.GetRecentCuts(12));
        Measure("Bekleyen sayısı", 50, () => repo.CountPendingSync());
        Measure("ID ile bul", 20, () => cuts.FindById("K1-500000"));
        Measure("Baskı kaydı", 100, () => print.Print("K1-500000"));
        Measure("Geçmiş: son 7 gün", 300, () => repo.Search(new CutQuery(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-6)), DateOnly.FromDateTime(DateTime.UtcNow), null, CutStatusFilter.All, 0, 200), tz));
        Measure("Geçmiş: tüm kayıtlar, ilk sayfa", 1000, () => repo.Search(new CutQuery(null, null, null, CutStatusFilter.All, 0, 200), tz));
        // Yaklaşık 5 yıllık verinin en son sayfası: nadir, arka planda çalışır. Bilinçli olarak 2,5 sn.
        Measure("Geçmiş: tüm kayıtlar, son sayfa", 2500, () => repo.Search(new CutQuery(null, null, null, CutStatusFilter.All, 4990, 200), tz));
        Measure("Geçmiş: tümünde lot araması", 1500, () => repo.Search(new CutQuery(null, null, "LOT-0250", CutStatusFilter.All, 0, 200), tz));
        Measure("Geçmiş: yalnızca iptaller", 1000, () => repo.Search(new CutQuery(null, null, null, CutStatusFilter.Voided, 0, 200), tz));
        Measure("Geçmiş: ID ile arama", 300, () => repo.Search(new CutQuery(null, null, "K1-500000", CutStatusFilter.All, 0, 200), tz));

        Measure("Gönderim paketi hazırlama (200)", 300, () => outbox.GetPendingBatch(200));
        Measure("Eski kuyruk temizliği", 300, () => outbox.PurgeSent(DateTimeOffset.UtcNow.AddDays(-30)));

        var backupDir = Path.Combine(Path.GetDirectoryName(db.Factory.DatabasePath)!, "yedek");
        var backup = new BackupService(db.Factory, backupDir, clock);
        sw.Restart();
        var path = backup.CreateBackup();
        Report($"Yedek + bütünlük kontrolü: {sw.Elapsed.TotalSeconds:0.0} sn, {new FileInfo(path).Length / 1024 / 1024} MB");

        sw.Restart();
        DatabaseMigrator.Migrate(db.Factory);
        Report($"Açılış göç kontrolü: {sw.Elapsed.TotalMilliseconds:0} ms");

        using var c = db.Factory.Open();
        Assert.Equal("ok", c.ExecuteScalar<string>("PRAGMA integrity_check"));
        Report("Bütünlük: ok");
        Assert.True(_overLimit.Count == 0, string.Join("; ", _overLimit));
    }

    [Fact]
    public async Task Long_offline_period_drains_without_loss()
    {
        if (!Enabled) return;

        using var db = new TempDatabase();
        DatabaseMigrator.Migrate(db.Factory);
        var settings = new SettingsRepository(db.Factory);
        settings.SetSyncTarget(Url, FakeAppsScript.Key);
        var cuts = new CutService(db.Factory, settings, TimeProvider.System);
        var outbox = new OutboxRepository(db.Factory);
        var sheet = new FakeAppsScript();
        var sync = new SyncService(outbox, settings, new StationCounters(db.Factory),
            new AppsScriptTransport(new HttpClient(sheet)), TimeProvider.System);

        const int count = 12_000;
        for (var i = 0; i < count; i++) cuts.CreateCut($"LOT-{i % 50}", 100 + i % 900);

        var sw = Stopwatch.StartNew();
        var runs = 0;
        while (outbox.CountPendingCuts() > 0)
        {
            Assert.Null((await sync.RunOnceAsync()).Failure);
            runs++;
        }
        Report($"{count:N0} bekleyen kayıt {runs} turda gönderildi: {sw.Elapsed.TotalSeconds:0.0} sn (sahte tabloyla, ağ yok)");
        Assert.Equal(count, sheet.Sheet.Count);
        Assert.Equal(count, sheet.Sheet.Select(r => r["cut_id"].GetString()).Distinct().Count());

        using var restored = new TempDatabase();
        DatabaseMigrator.Migrate(restored.Factory);
        var restore = new RestoreService(restored.Factory, new AppsScriptTransport(new HttpClient(sheet)), TimeProvider.System);
        sw.Restart();
        var result = await restore.RestoreAsync(new SyncTarget(Url, FakeAppsScript.Key));
        Report($"{count:N0} kayıt geri kurma: {sw.Elapsed.TotalSeconds:0.0} sn");
        Assert.Equal(count, result.Cuts);

        using var a = db.Factory.Open();
        using var b = restored.Factory.Open();
        Assert.Equal(a.ExecuteScalar<long>("SELECT sum(length_cm) FROM cuts"), b.ExecuteScalar<long>("SELECT sum(length_cm) FROM cuts"));
    }

    private sealed class NullPrinter : ILabelPrinter
    {
        public string DisplayName => "yok";
        public bool IsPhysical => false;
        public void Print(LabelData label) { }
    }
}
