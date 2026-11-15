using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Server.Data;
using Microsoft.Data.Sqlite;
using static KumasBarkod.Server.Tests.Rows;

namespace KumasBarkod.Server.Tests;

/// <summary>Sunucu yedekleri: saatlik (48), günlük (30), ikinci klasör.</summary>
public sealed class ServerBackupTests : IDisposable
{
    private readonly ServerFixture _server = new();
    private readonly string _mirror;
    private readonly LocalClock _clock = new();

    public ServerBackupTests()
    {
        _mirror = Path.Combine(_server.DataDirectory, "usb");
    }

    public void Dispose() => _server.Dispose();

    private ServerBackupService Backup(string? mirror = null) =>
        new(_server.Db, new ServerPaths(_server.DataDirectory), _clock, mirror);   // Db: sunucu başlar, veri klasörü oluşur

    private static string[] Names(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray() : [];

    [Fact]
    public async Task First_run_takes_hourly_and_daily_backup_with_data()
    {
        await _server.PostOk(Batch("PC-1", "K1", Cut("K1", 1), Cut("K1", 2)));
        _clock.Local = new DateTime(2026, 11, 15, 14, 20, 0);

        var r = Backup().RunDue();

        Assert.EndsWith(Path.Combine("saatlik", "sunucu-2026-11-15-14.db"), r.Hourly);
        Assert.EndsWith(Path.Combine("gunluk", "sunucu-2026-11-15.db"), r.Daily);
        Assert.Null(r.MirrorError);
        using var c = new SqliteConnection($"Data Source={r.Daily};Mode=ReadOnly;Pooling=False");
        c.Open();
        Assert.Equal(2L, c.ExecuteScalar<long>("SELECT count(*) FROM rolls"));
        Assert.Equal("ok", c.ExecuteScalar<string>("PRAGMA integrity_check"));
        Assert.Equal("delete", c.ExecuteScalar<string>("PRAGMA journal_mode"));   // tek dosya
    }

    [Fact]
    public void One_backup_per_hour_and_one_daily_per_day()
    {
        var b = Backup();
        _clock.Local = new DateTime(2026, 11, 15, 9, 0, 0);
        Assert.NotNull(b.RunDue().Hourly);

        _clock.Local = new DateTime(2026, 11, 15, 9, 55, 0);
        var same = b.RunDue();
        Assert.Null(same.Hourly);
        Assert.Null(same.Daily);

        _clock.Local = new DateTime(2026, 11, 15, 10, 1, 0);
        var next = b.RunDue();
        Assert.NotNull(next.Hourly);
        Assert.Null(next.Daily);

        _clock.Local = new DateTime(2026, 11, 16, 0, 5, 0);
        Assert.NotNull(b.RunDue().Daily);

        Assert.Equal(["sunucu-2026-11-15-09.db", "sunucu-2026-11-15-10.db", "sunucu-2026-11-16-00.db"], Names(b.HourlyDirectory));
        Assert.Equal(["sunucu-2026-11-15.db", "sunucu-2026-11-16.db"], Names(b.DailyDirectory));
    }

    [Fact]
    public void Old_backups_are_pruned_but_other_files_kept()
    {
        var b = Backup();
        Directory.CreateDirectory(b.HourlyDirectory);
        Directory.CreateDirectory(b.DailyDirectory);
        File.WriteAllText(Path.Combine(b.HourlyDirectory, "beni-silme.txt"), "x");
        // 34 günlük eski yedek (içerik önemsiz; silme ada göre)
        var start = new DateTime(2026, 11, 1, 0, 0, 0);
        for (var h = 0; h < 24 * 34; h++)
            File.WriteAllText(Path.Combine(b.HourlyDirectory, $"sunucu-{start.AddHours(h):yyyy-MM-dd-HH}.db"), "");
        for (var d = 0; d < 34; d++)
            File.WriteAllText(Path.Combine(b.DailyDirectory, $"sunucu-{start.AddDays(d):yyyy-MM-dd}.db"), "");

        _clock.Local = start.AddDays(34).AddHours(5);   // 2026-12-05 05:00
        b.RunDue();

        var hourly = Names(b.HourlyDirectory).Where(n => n.EndsWith(".db")).ToArray();
        Assert.Equal(ServerBackupService.KeepHourly, hourly.Length);
        Assert.Equal("sunucu-2026-12-05-05.db", hourly[^1]);
        Assert.Equal("sunucu-2026-12-03-01.db", hourly[0]);   // sahte dosyalar 12-04 23:00 de bitiyor + yeni 12-05 05:00
        var daily = Names(b.DailyDirectory);
        Assert.Equal(ServerBackupService.KeepDaily, daily.Length);
        Assert.Equal("sunucu-2026-11-06.db", daily[0]);
        Assert.Equal("sunucu-2026-12-05.db", daily[^1]);
        Assert.Contains("beni-silme.txt", Names(b.HourlyDirectory));
    }

    [Fact]
    public void Mirror_receives_copies_and_catches_up_after_being_unavailable()
    {
        // İkinci klasör bir süre yok (USB disk çıkarılmış): yerel yedek yine alınır, hata bildirilir
        var backup = Backup();   // sunucuyu başlat (veri klasörü)
        var unreachable = Path.Combine(_server.DataDirectory, "dosya-engel");
        File.WriteAllText(unreachable, "klasör yerine dosya");
        _clock.Local = new DateTime(2026, 11, 15, 9, 0, 0);
        var broken = Backup(Path.Combine(unreachable, "alt")).RunDue();
        Assert.NotNull(broken.Hourly);
        Assert.Contains("İkinci yedek klasörüne yazılamadı", broken.MirrorError);

        // Disk geri takıldı: eksik olan bütün yedekler kopyalanır
        _clock.Local = new DateTime(2026, 11, 15, 10, 0, 0);
        var ok = Backup(_mirror).RunDue();

        Assert.Null(ok.MirrorError);
        Assert.Equal(["sunucu-2026-11-15-09.db", "sunucu-2026-11-15-10.db"], Names(Path.Combine(_mirror, "saatlik")));
        Assert.Equal(["sunucu-2026-11-15.db"], Names(Path.Combine(_mirror, "gunluk")));
        Assert.Equal(3, ok.Mirrored.Count);
        Assert.Empty(Backup(_mirror).RunDue().Mirrored);   // tekrar kopyalanmaz
    }

    [Fact]
    public async Task Backups_are_written_while_server_is_receiving()
    {
        var b = Backup();
        var stop = false;
        var writer = Task.Run(async () =>
        {
            var seq = 1;
            while (!Volatile.Read(ref stop))
                await _server.PostOk(Batch("PC-1", "K1", Cut("K1", seq++)));
        });

        for (var h = 0; h < 5; h++)
        {
            _clock.Local = new DateTime(2026, 11, 15, 8 + h, 0, 0);
            b.RunDue();
        }
        Volatile.Write(ref stop, true);
        await writer;

        foreach (var file in b.List())
        {
            using var c = new SqliteConnection($"Data Source={file.FullName};Mode=ReadOnly;Pooling=False");
            c.Open();
            Assert.Equal("ok", c.ExecuteScalar<string>("PRAGMA integrity_check"));
        }
        Assert.Empty(_server.Invariants());
    }

    /// <summary>Yerel saati doğrudan ayarlanan saat (yedek adları yerel saatle).</summary>
    private sealed class LocalClock : TimeProvider
    {
        public DateTime Local { get; set; } = new(2026, 11, 15, 9, 0, 0);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => new(Local, TimeSpan.Zero);
    }
}
