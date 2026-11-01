using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Diagnostics;
using KumasBarkod.Core.Services;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Tests.Data;

public class BackupServiceTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly SettableClock _clock = new();
    private readonly string _backupDir;
    private readonly BackupService _backup;
    private readonly CutService _cuts;

    public BackupServiceTests()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        _backupDir = Path.Combine(Path.GetDirectoryName(_db.Factory.DatabasePath)!, "backups");
        _backup = new BackupService(_db.Factory, _backupDir, _clock);
        _cuts = new CutService(_db.Factory, new SettingsRepository(_db.Factory), _clock);
    }

    public void Dispose() => _db.Dispose();

    private static long CountCuts(string dbPath)
    {
        using var c = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        c.Open();
        return c.ExecuteScalar<long>("SELECT count(*) FROM cuts");
    }

    [Fact]
    public void Backup_contains_latest_data_even_before_checkpoint()
    {
        using var keepOpen = _db.Factory.Open();   // WAL ana dosyaya aktarılmasın
        _cuts.CreateCut("LOT-A", 100);
        _cuts.CreateCut("LOT-A", 200);

        var path = _backup.CreateBackup();

        Assert.EndsWith("kumas-2026-11-01.db", path);
        Assert.Equal(2, CountCuts(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(File.Exists(path + "-wal"));   // yedek tek dosya
    }

    [Fact]
    public void Daily_backup_is_taken_once_per_day()
    {
        Assert.NotNull(_backup.EnsureDailyBackup());
        Assert.Null(_backup.EnsureDailyBackup());

        _clock.Now = _clock.Now.AddDays(1);
        Assert.NotNull(_backup.EnsureDailyBackup());
        Assert.Equal(2, _backup.List().Count);
    }

    [Fact]
    public void Old_backups_are_pruned_after_30_days()
    {
        for (var day = 0; day < 40; day++)
        {
            _backup.EnsureDailyBackup();
            _clock.Now = _clock.Now.AddDays(1);
        }

        var list = _backup.List();
        Assert.Equal(31, list.Count);   // bugün + önceki 30 gün
        Assert.Equal(new DateOnly(2026, 12, 10), list[0].Day);    // 1 Kasım + 39 gün
        Assert.Equal(new DateOnly(2026, 11, 10), list[^1].Day);   // 30 gün öncesi
    }

    [Fact]
    public void Unrelated_files_in_backup_folder_are_left_alone()
    {
        Directory.CreateDirectory(_backupDir);
        File.WriteAllText(Path.Combine(_backupDir, "notlar.txt"), "x");
        File.WriteAllText(Path.Combine(_backupDir, "kumas-elle.db"), "x");
        _clock.Now = _clock.Now.AddDays(100);

        _backup.EnsureDailyBackup();

        Assert.True(File.Exists(Path.Combine(_backupDir, "notlar.txt")));
        Assert.True(File.Exists(Path.Combine(_backupDir, "kumas-elle.db")));
    }

    [Fact]
    public void Backup_can_be_opened_as_a_working_database()
    {
        _cuts.CreateCut("LOT-A", 100);
        var path = _backup.CreateBackup();

        // Geri yükleme senaryosu: yedek kopyalanıp uygulama onunla açılabilmeli
        var restored = new SqliteConnectionFactory(Path.Combine(Path.GetDirectoryName(path)!, "geri", "kumas.db"));
        File.Copy(path, restored.DatabasePath);
        Assert.Equal(0, DatabaseMigrator.Migrate(restored));
        var cuts = new CutService(restored, new SettingsRepository(restored), _clock);
        Assert.Equal("K1-000002", cuts.CreateCut("LOT-A", 100).CutId);
    }

    // ---------- Günlük ----------

    [Fact]
    public void Log_writes_daily_file_with_level_and_exception()
    {
        var dir = Path.Combine(_backupDir, "..", "logs");
        var log = new FileLog(dir, _clock);

        log.Info("Uygulama açıldı");
        log.Error("Baskı başarısız", new InvalidOperationException("Kağıt bitti"));

        var text = File.ReadAllText(log.CurrentFile);
        Assert.EndsWith("kumas-2026-11-01.log", log.CurrentFile);
        Assert.Contains("[BİLGİ] Uygulama açıldı", text);
        Assert.Contains("[HATA] Baskı başarısız", text);
        Assert.Contains("Kağıt bitti", text);
    }

    [Fact]
    public void Log_prunes_files_older_than_30_days()
    {
        var dir = Path.Combine(_backupDir, "..", "logs2");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "kumas-2026-09-01.log"), "eski");
        File.WriteAllText(Path.Combine(dir, "kumas-2026-10-15.log"), "yeni");

        _ = new FileLog(dir, _clock);

        Assert.False(File.Exists(Path.Combine(dir, "kumas-2026-09-01.log")));
        Assert.True(File.Exists(Path.Combine(dir, "kumas-2026-10-15.log")));
    }

    [Fact]
    public void Log_failure_does_not_throw()
    {
        // Klasör yerine dosya: yazılamaz
        var blocker = Path.Combine(_backupDir, "..", "engel");
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        File.WriteAllText(blocker, "x");
        var log = new FileLog(blocker, _clock);

        log.Error("deneme", new Exception("x"));   // atmamalı
    }

    private sealed class SettableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
