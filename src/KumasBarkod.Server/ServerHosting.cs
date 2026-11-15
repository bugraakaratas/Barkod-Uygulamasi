using System.Reflection;
using KumasBarkod.Core.Diagnostics;

namespace KumasBarkod.Server;

public static class ServerInfo
{
    public const string ServiceName = "KumasBarkodSunucu";
    public const string ServiceId = "KumasBarkod.Server";

    public static string Version { get; } =
        typeof(ServerInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ServerInfo).Assembly.GetName().Version?.ToString() ?? "0";
}

/// <summary>
/// Sunucunun diskteki yerleri. Varsayılan <c>%ProgramData%\KumasBarkodSunucu</c> (Windows hizmeti
/// kullanıcıdan bağımsız çalışır). <c>KumasBarkod:DataDirectory</c> ayarı veya
/// <c>KUMASBARKOD_SERVER_DATA_DIR</c> ortam değişkeni ile değişir (testler gerçek veriye yazmasın).
/// </summary>
public sealed record ServerPaths(string DataDirectory)
{
    public const string ConfigKey = "KumasBarkod:DataDirectory";
    public const string EnvironmentVariable = "KUMASBARKOD_SERVER_DATA_DIR";

    public string DatabasePath => Path.Combine(DataDirectory, "sunucu.db");
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static ServerPaths From(IConfiguration? configuration)
    {
        var dir = configuration?[ConfigKey];
        if (string.IsNullOrWhiteSpace(dir)) dir = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ServerInfo.ServiceName);
        return new ServerPaths(Path.GetFullPath(dir));
    }
}

/// <summary>ASP.NET günlüğünü uygulamanın günlük dosyasına (<c>logs\kumas-YYYY-MM-DD.log</c>) da yazar: uyarı ve üstü + kendi bilgilerimiz.</summary>
public sealed class FileLogProvider(ILog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(log, categoryName);

    public void Dispose() { }

    private sealed class FileLogger(ILog log, string category) : ILogger
    {
        private readonly bool _ours = category.StartsWith("KumasBarkod", StringComparison.Ordinal)
                                      || category == "Program";

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning || (_ours && level >= LogLevel.Information);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var message = formatter(state, exception);
            switch (level)
            {
                case >= LogLevel.Error: log.Error(message, exception); break;
                case LogLevel.Warning: log.Warn(message); break;
                default: log.Info(message); break;
            }
        }
    }
}
