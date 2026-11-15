using KumasBarkod.Core.Data;
using KumasBarkod.Core.Diagnostics;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Server;
using KumasBarkod.Server.Data;
using KumasBarkod.Server.Ingest;

// Yönetim komutları (hizmet çalışırken de güvenli: tek işlem, kısa kilit).
//   KumasBarkodSunucu.exe --istasyon-serbest-birak K1   → bilgisayarı değişen istasyon kodunu serbest bırakır
//   KumasBarkodSunucu.exe --kontrol                     → veri bütünlüğü kontrolü
if (AdminCommands.TryRun(args, out var exitCode))
    return exitCode;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Host.UseWindowsService(o => o.ServiceName = ServerInfo.ServiceName);

if (builder.Configuration["Urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls($"http://0.0.0.0:{ServerApiContract.DefaultPort}");

// Bütün dosya yolları ServerPaths'ten (testler yalnızca bunu değiştirir, gerçek veriye dokunulmaz).
builder.Services.AddSingleton(sp => ServerPaths.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<ILog>(sp => new FileLog(sp.GetRequiredService<ServerPaths>().LogDirectory, TimeProvider.System));
builder.Services.AddSingleton<ILoggerProvider>(sp => new FileLogProvider(sp.GetRequiredService<ILog>()));
builder.Services.AddSingleton(sp => new SqliteConnectionFactory(sp.GetRequiredService<ServerPaths>().DatabasePath));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CutIngestService>();
builder.Services.AddSingleton(sp => new BackupService(
    sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<ServerPaths>().BackupDirectory,
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = ServerApiContract.Json.PropertyNamingPolicy;
});

var app = builder.Build();

// Şema: açılışta en son sürüme getirilir; veritabanı daha yeni sürümdense sunucu açılmaz (veri bozulmasın).
DatabaseMigrator.Migrate(app.Services.GetRequiredService<SqliteConnectionFactory>(), ServerMigrations.All);
app.Logger.LogInformation("Sunucu açılıyor (sürüm {Version}, veri: {Data})",
    ServerInfo.Version, app.Services.GetRequiredService<ServerPaths>().DataDirectory);

app.UseMiddleware<ApiErrorMiddleware>();
app.MapServerEndpoints();

app.Run();
return 0;

/// <summary>Testlerde <c>WebApplicationFactory&lt;Program&gt;</c> için.</summary>
public partial class Program;
