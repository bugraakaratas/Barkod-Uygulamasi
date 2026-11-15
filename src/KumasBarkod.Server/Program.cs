using KumasBarkod.Core.Data;
using KumasBarkod.Core.Diagnostics;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Server;
using KumasBarkod.Server.Data;
using KumasBarkod.Server.Ingest;
using KumasBarkod.Server.Lots;
using KumasBarkod.Server.Stock;

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

// Fabrikaya özel ayarlar (ör. ikinci yedek klasörü, kapı) veri klasöründeki ayarlar.json'da:
// program klasörü güncellemede silinir, veri klasörü kalır.
builder.Configuration.AddJsonFile(
    Path.Combine(ServerPaths.From(builder.Configuration).DataDirectory, ServerPaths.SettingsFileName), optional: true, reloadOnChange: false);

// Dinlenen adres: --urls, ASPNETCORE_URLS ya da ayarlar.json "Urls"; yoksa bütün ağ kartlarında 5080.
// "*" = IPv4 + IPv6 birlikte. Yalnızca IPv4 (0.0.0.0) dinlenirse Windows ad çözümünde önce IPv6'yı dener
// ve her YENİ bağlantı ~2 sn gecikir (ölçüldü: 5 istek 10,2 sn → ikisinde birden 20 ms).
var urls = builder.Configuration["Urls"];
builder.WebHost.UseUrls(string.IsNullOrWhiteSpace(urls) ? $"http://*:{ServerApiContract.DefaultPort}" : urls);

// Bütün dosya yolları ServerPaths'ten (testler yalnızca bunu değiştirir, gerçek veriye dokunulmaz).
builder.Services.AddSingleton(sp => ServerPaths.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<ILog>(sp => new FileLog(sp.GetRequiredService<ServerPaths>().LogDirectory, TimeProvider.System));
builder.Services.AddSingleton<ILoggerProvider>(sp => new FileLogProvider(sp.GetRequiredService<ILog>()));
builder.Services.AddSingleton(sp => new SqliteConnectionFactory(sp.GetRequiredService<ServerPaths>().DatabasePath));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CutIngestService>();
builder.Services.AddSingleton<LotService>();
builder.Services.AddSingleton<StockQueryService>();
builder.Services.AddSingleton<KumasBarkod.Server.Auth.AuthService>();
builder.Services.AddSingleton<KumasBarkod.Server.Shipments.ShipmentService>();
builder.Services.AddSingleton(sp => new ServerBackupService(
    sp.GetRequiredService<SqliteConnectionFactory>(), sp.GetRequiredService<ServerPaths>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IConfiguration>()[ServerPaths.BackupMirrorConfigKey]));
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
app.MapShipmentEndpoints();

app.Run();
return 0;

/// <summary>Testlerde <c>WebApplicationFactory&lt;Program&gt;</c> için.</summary>
public partial class Program;
