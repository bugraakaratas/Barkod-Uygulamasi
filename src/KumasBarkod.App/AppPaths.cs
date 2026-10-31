using System.IO;

namespace KumasBarkod.App;

/// <summary>Uygulamanın diskteki dosya yerleri.</summary>
public static class AppPaths
{
    /// <summary>
    /// Varsayılan: %LOCALAPPDATA%\KumasBarkod.
    /// Test ve geliştirme için KUMASBARKOD_DATA_DIR ortam değişkeniyle değiştirilebilir;
    /// böylece denemeler gerçek veritabanına kayıt yazmaz.
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("KUMASBARKOD_DATA_DIR") is { Length: > 0 } overridden
            ? Path.GetFullPath(overridden)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KumasBarkod");

    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "kumas.db");
}
