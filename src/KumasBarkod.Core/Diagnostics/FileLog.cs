using System.Globalization;
using System.Text;

namespace KumasBarkod.Core.Diagnostics;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

/// <summary>Testler ve günlük istenmeyen yerler için.</summary>
public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}

/// <summary>
/// Hata günlüğü: günde bir dosya (<c>kumas-YYYY-MM-DD.log</c>), satır başında yerel saat ve seviye.
/// Son 30 günün dosyaları saklanır. Günlük yazılamazsa uygulama durmaz (hata yutulur).
/// İş parçacığı güvenli.
/// </summary>
public sealed class FileLog : ILog
{
    public const int KeepDays = 30;

    private readonly string _directory;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    public FileLog(string directory, TimeProvider clock)
    {
        _directory = directory;
        _clock = clock;
        try { Prune(); } catch { /* günlük temizliği uygulamayı durdurmaz */ }
    }

    public string Directory => _directory;

    public string CurrentFile => Path.Combine(_directory, $"kumas-{Now():yyyy-MM-dd}.log");

    public void Info(string message) => Write("BİLGİ", message, null);
    public void Warn(string message) => Write("UYARI", message, null);
    public void Error(string message, Exception? exception = null) => Write("HATA", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(Now().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(level).Append("] ")
            .Append(message);
        if (exception is not null)
            line.AppendLine().Append("    ").Append(exception.ToString().Replace("\n", "\n    "));
        line.AppendLine();

        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                File.AppendAllText(CurrentFile, line.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Disk dolu / izin yok: günlük yazılamaması uygulamayı durdurmamalı.
            }
        }
    }

    private void Prune()
    {
        if (!System.IO.Directory.Exists(_directory)) return;
        var limit = DateOnly.FromDateTime(Now().DateTime).AddDays(-KeepDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "kumas-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file)["kumas-".Length..];
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < limit)
            {
                File.Delete(file);
            }
        }
    }

    private DateTimeOffset Now() => _clock.GetLocalNow();
}
