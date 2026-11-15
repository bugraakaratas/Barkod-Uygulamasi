using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Server.Data;

namespace KumasBarkod.Server;

/// <summary>
/// Arka plan bakımı: açılışta ve saatte bir yedek (<see cref="ServerBackupService"/>: saatlik + günlük +
/// ikinci klasör) ve günde bir veri bütünlüğü kontrolü (sorun varsa günlüğe HATA yazılır).
/// </summary>
public sealed class MaintenanceWorker(
    ServerBackupService backup,
    SqliteConnectionFactory db,
    ILogger<MaintenanceWorker> log) : BackgroundService
{
    /// <summary>Kontrol aralığı; yedek saat başına bir kez alınır (saat değişince ilk kontrolde).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            RunOnce();
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal void RunOnce()
    {
        try
        {
            var result = backup.RunDue();
            if (result.Hourly is not null) log.LogInformation("Saatlik yedek alındı: {Path}", result.Hourly);
            if (result.MirrorError is not null) log.LogError("{Error}", result.MirrorError);
            if (result.Daily is not null)
            {
                log.LogInformation("Günlük yedek: {Path}", result.Daily);
                CheckIntegrity();   // günde bir
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Yedek alınamadı");
        }
    }

    private void CheckIntegrity()
    {
        try
        {
            var problems = ServerInvariants.Check(db);
            if (problems.Count == 0)
            {
                log.LogInformation("Veri bütünlüğü kontrolü: sorun yok");
                return;
            }
            log.LogError("Veri bütünlüğü kontrolü: {Count} sorun. İlkleri: {First}",
                problems.Count, string.Join(" | ", problems.Take(5)));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Veri bütünlüğü kontrolü çalışmadı");
        }
    }
}

/// <summary>Komut satırından yönetim işleri (hizmet ayrı süreçte çalışırken de güvenli).</summary>
public static class AdminCommands
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;

        var paths = ServerPaths.From(null);
        switch (args[0])
        {
            case "--istasyon-serbest-birak" when args.Length == 2:
                exitCode = ReleaseStation(paths, args[1].Trim().ToUpperInvariant());
                return true;
            case "--kontrol":
                exitCode = Check(paths);
                return true;
            default:
                return false;   // ASP.NET'in kendi argümanları (--urls vb.)
        }
    }

    internal static int ReleaseStation(ServerPaths paths, string code)
    {
        var db = new SqliteConnectionFactory(paths.DatabasePath);
        DatabaseMigrator.Migrate(db, ServerMigrations.All);
        using var c = db.Open();
        var owner = c.ExecuteScalar<string?>("SELECT install_id FROM stations WHERE station_code = @code", new { code });
        if (owner is null)
        {
            Console.WriteLine($"{code} sunucuda kayıtlı değil.");
            return 1;
        }
        c.Execute("DELETE FROM stations WHERE station_code = @code", new { code });
        new KumasBarkod.Core.Diagnostics.FileLog(paths.LogDirectory, TimeProvider.System)
            .Warn($"İstasyon kaydı serbest bırakıldı: {code} (önceki bilgisayar {owner})");
        Console.WriteLine($"{code} serbest bırakıldı. Bu kodla ilk gönderim yapan bilgisayar kaydı alır.");
        return 0;
    }

    private static int Check(ServerPaths paths)
    {
        var db = new SqliteConnectionFactory(paths.DatabasePath);
        DatabaseMigrator.Migrate(db, ServerMigrations.All);
        var problems = ServerInvariants.Check(db);
        Console.WriteLine(problems.Count == 0 ? "Veri bütünlüğü: sorun yok." : $"Veri bütünlüğü: {problems.Count} sorun");
        foreach (var p in problems.Take(50)) Console.WriteLine("  " + p);
        return problems.Count == 0 ? 0 : 2;
    }
}
