using System.Globalization;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Core.Data;

public sealed record BackupInfo(string Path, DateOnly Day, long SizeBytes);

/// <summary>
/// Günlük yerel yedek: SQLite Backup API ile (uygulama açıkken de tutarlı kopya) ayrı klasöre
/// <c>kumas-YYYY-MM-DD.db</c>. Son 30 gün saklanır. Yedek önce geçici dosyaya yazılır, bütünlüğü
/// kontrol edilir, sonra adlandırılır: yarım kalan yedek asla "yedek" görünmez.
/// Bu yedek bozulan dosyaya ve yanlış işleme karşıdır; AYNI DİSKTE olduğu için disk arızasına
/// karşı değildir (ona karşı Google Sheets kopyası var).
/// </summary>
public sealed class BackupService(SqliteConnectionFactory db, string backupDirectory, TimeProvider clock)
{
    public const int KeepDays = 30;
    private const string Prefix = "kumas-";
    private const string Extension = ".db";

    public string BackupDirectory { get; } = backupDirectory;

    /// <summary>Bugünün yedeği yoksa alır ve eskileri temizler. Alınan yedeğin yolu; zaten varsa null.</summary>
    public string? EnsureDailyBackup()
    {
        var today = Today();
        if (File.Exists(PathFor(today))) return null;

        var path = CreateBackup();
        Prune();
        return path;
    }

    /// <summary>Bugünün tarihiyle yedek alır (varsa üzerine yazar).</summary>
    public string CreateBackup()
    {
        Directory.CreateDirectory(BackupDirectory);
        var path = PathFor(Today());
        var temp = path + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);

        using (var source = db.Open())
        using (var target = new SqliteConnection($"Data Source={temp};Pooling=False"))
        {
            target.Open();
            source.BackupDatabase(target);

            // Yedek WAL ayarını da kopyalar; açılınca yanında -wal dosyası oluşmasın diye tek dosya kipine al.
            using (var mode = target.CreateCommand())
            {
                mode.CommandText = "PRAGMA journal_mode = DELETE";
                mode.ExecuteNonQuery();
            }

            using var check = target.CreateCommand();
            check.CommandText = "PRAGMA integrity_check";
            var result = check.ExecuteScalar() as string;
            if (result != "ok")
                throw new InvalidOperationException($"Yedek doğrulanamadı: {result}");
        }

        File.Move(temp, path, overwrite: true);
        return path;
    }

    /// <summary>30 günden eski yedekleri siler. Silinen sayısı.</summary>
    public int Prune()
    {
        var limit = Today().AddDays(-KeepDays);
        var deleted = 0;
        foreach (var backup in List().Where(b => b.Day < limit))
        {
            File.Delete(backup.Path);
            deleted++;
        }
        return deleted;
    }

    /// <summary>Mevcut yedekler, yeniden eskiye.</summary>
    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(BackupDirectory)) return [];
        return Directory.EnumerateFiles(BackupDirectory, Prefix + "*" + Extension)
            .Select(path => (path, day: ParseDay(Path.GetFileNameWithoutExtension(path))))
            .Where(x => x.day is not null)
            .Select(x => new BackupInfo(x.path, x.day!.Value, new FileInfo(x.path).Length))
            .OrderByDescending(b => b.Day)
            .ToList();
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private string PathFor(DateOnly day) =>
        Path.Combine(BackupDirectory, Prefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + Extension);

    private static DateOnly? ParseDay(string name) =>
        name.StartsWith(Prefix, StringComparison.Ordinal)
        && DateOnly.TryParseExact(name[Prefix.Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}
