using System.IO;

namespace KumasBarkod.App;

/// <summary>Uygulamanın diskteki dosya yerleri.</summary>
public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\KumasBarkod</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KumasBarkod");

    public static string DatabasePath { get; } = Path.Combine(DataDirectory, "kumas.db");
}
