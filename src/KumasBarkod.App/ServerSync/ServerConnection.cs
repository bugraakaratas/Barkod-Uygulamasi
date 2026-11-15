using System.IO;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;
using Microsoft.Win32;

namespace KumasBarkod.App.ServerSync;

/// <summary>
/// Depo sunucusu bağlantısı: kuyruk (<c>server_outbox</c>), gönderim servisi ve arka plan göndericisi.
/// Sheets göndericisinden bağımsızdır; biri hata verse de diğeri çalışır.
/// Yerel ağdaki sunucu için kısa aralıklar: normalde 30 sn, yeni kayıttan 1 sn sonra, hata beklemesi en fazla 1 dk.
/// </summary>
public sealed class ServerConnection : IAsyncDisposable
{
    public static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan NudgeDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);

    public ServerConnection(SqliteConnectionFactory db, SettingsRepository settings, StationCounters counters,
        IServerTransport transport, StationIdentity identity, TimeProvider clock)
    {
        Outbox = new OutboxRepository(db, OutboxTables.Server);
        Service = new ServerSyncService(Outbox, settings, counters, transport, identity, clock);
        Worker = new SyncWorker(Service, Outbox, clock, NormalInterval, NudgeDelay, MaxBackoff);
    }

    public OutboxRepository Outbox { get; }
    public ServerSyncService Service { get; }
    public SyncWorker Worker { get; }

    public ValueTask DisposeAsync() => Worker.DisposeAsync();
}

/// <summary>
/// Bilgisayarın kalıcı kimliği: Windows MachineGuid (yeniden kurulumda ve veritabanı silinince de aynı kalır;
/// veritabanı başka bilgisayara kopyalanınca değişir — bu bilerek). Okunamazsa bir kez üretilip
/// veri klasörünün DIŞINDA (%LOCALAPPDATA%\KumasBarkod\install-id) saklanır.
/// </summary>
public static class StationIdentityProvider
{
    public static StationIdentity Create() =>
        new(ReadMachineGuid() ?? FallbackId(), typeof(StationIdentityProvider).Assembly.GetName().Version?.ToString(3));

    private static string? ReadMachineGuid()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") is string { Length: > 0 } guid ? "mg-" + guid.ToLowerInvariant() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string FallbackId()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KumasBarkod", "install-id");
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } saved) return saved;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var id = "id-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(path, id);
            return id;
        }
        catch (IOException)
        {
            return "tmp-" + Environment.MachineName.ToLowerInvariant();
        }
    }
}
