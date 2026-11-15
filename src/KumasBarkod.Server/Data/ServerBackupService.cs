using System.Globalization;
using KumasBarkod.Core.Data;

namespace KumasBarkod.Server.Data;

public sealed record ServerBackupResult(string? Hourly, string? Daily, IReadOnlyList<string> Mirrored, string? MirrorError);

/// <summary>
/// Sunucu yedekleri. Sevkiyat kayıtları yalnızca sunucuda olduğu için sık yedek gerekir:
/// <list type="bullet">
/// <item><b>Saatlik</b> <c>backups\saatlik\sunucu-YYYY-MM-DD-HH.db</c>, son <see cref="KeepHourly"/> adet (2 gün).
/// Disk bozulursa kayıp en fazla 1 saat.</item>
/// <item><b>Günlük</b> <c>backups\gunluk\sunucu-YYYY-MM-DD.db</c>, son <see cref="KeepDaily"/> gün. Fark geç anlaşılan
/// yanlış işlemlere karşı (ör. 3 gün önceki hâle bakmak).</item>
/// <item><b>İkinci klasör</b> (isteğe bağlı, <c>KumasBarkod:BackupMirrorDirectory</c>): her yeni yedek oraya da kopyalanır
/// (USB disk, ağ diski). Aynı diskteki yedek disk arızasına karşı korumaz; ikinci klasör korur.</item>
/// </list>
/// Yedek Backup API ile alınır: sunucu çalışırken, okutmayı durdurmadan, tutarlı kopya. İsimler yerel saatle.
/// </summary>
public sealed class ServerBackupService(SqliteConnectionFactory db, ServerPaths paths, TimeProvider clock, string? mirrorDirectory)
{
    public const int KeepHourly = 48;
    public const int KeepDaily = 30;
    public const string HourlyFolder = "saatlik";
    public const string DailyFolder = "gunluk";
    private const string Prefix = "sunucu-";

    public string HourlyDirectory => Path.Combine(paths.BackupDirectory, HourlyFolder);
    public string DailyDirectory => Path.Combine(paths.BackupDirectory, DailyFolder);
    public string? MirrorDirectory { get; } = string.IsNullOrWhiteSpace(mirrorDirectory) ? null : mirrorDirectory;

    /// <summary>
    /// Bu saatin yedeği yoksa alır; bugünün günlük yedeği yoksa saatlik yedekten kopyalar; eskileri siler;
    /// yenileri ikinci klasöre kopyalar. Saatte bir (ve açılışta) çağrılır.
    /// </summary>
    public ServerBackupResult RunDue()
    {
        var now = clock.GetLocalNow();
        var hourName = Prefix + now.ToString("yyyy-MM-dd-HH", CultureInfo.InvariantCulture) + ".db";
        var dayName = Prefix + now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".db";
        var hourPath = Path.Combine(HourlyDirectory, hourName);
        var dayPath = Path.Combine(DailyDirectory, dayName);

        string? hourly = null, daily = null;
        if (!File.Exists(hourPath))
        {
            Directory.CreateDirectory(HourlyDirectory);
            BackupService.WriteVerifiedCopy(db, hourPath);
            hourly = hourPath;
        }
        if (!File.Exists(dayPath))
        {
            Directory.CreateDirectory(DailyDirectory);
            CopyAtomic(hourPath, dayPath);
            daily = dayPath;
        }

        Prune(HourlyDirectory, KeepHourly);
        Prune(DailyDirectory, KeepDaily);

        var (mirrored, mirrorError) = Mirror();
        return new ServerBackupResult(hourly, daily, mirrored, mirrorError);
    }

    /// <summary>Yerel yedekler (saatlik + günlük), yeniden eskiye.</summary>
    public IReadOnlyList<FileInfo> List() =>
        new[] { HourlyDirectory, DailyDirectory }
            .Where(Directory.Exists)
            .SelectMany(d => new DirectoryInfo(d).EnumerateFiles(Prefix + "*.db"))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();

    private (IReadOnlyList<string>, string?) Mirror()
    {
        if (MirrorDirectory is null) return ([], null);
        var copied = new List<string>();
        try
        {
            // Yalnızca yeni yedekler değil, ikinci klasörde EKSİK olan her yedek kopyalanır:
            // USB disk bir süre takılı değilse geri takılınca açık kapanır.
            foreach (var (local, folder, keep) in new[] { (HourlyDirectory, HourlyFolder, KeepHourly), (DailyDirectory, DailyFolder, KeepDaily) })
            {
                if (!Directory.Exists(local)) continue;
                var target = Path.Combine(MirrorDirectory, folder);
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(local, Prefix + "*.db"))
                {
                    var dest = Path.Combine(target, Path.GetFileName(file));
                    if (File.Exists(dest)) continue;
                    CopyAtomic(file, dest);
                    copied.Add(dest);
                }
                Prune(target, keep);
            }
            return (copied, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (copied, $"İkinci yedek klasörüne yazılamadı ({MirrorDirectory}): {ex.Message}");
        }
    }

    private static void CopyAtomic(string source, string dest)
    {
        var temp = dest + ".tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, dest, overwrite: true);
    }

    /// <summary>Ada göre (tarih sırası) en yeni <paramref name="keep"/> dosya kalır; klasördeki başka dosyalara dokunulmaz.</summary>
    private static void Prune(string directory, int keep)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var old in Directory.EnumerateFiles(directory, Prefix + "*.db")
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                     .Skip(keep))
            File.Delete(old);
    }
}
