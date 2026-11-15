using System.Net.Http.Json;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;
using KumasBarkod.Server;
using KumasBarkod.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace KumasBarkod.Server.Tests;

/// <summary>Elle ilerletilen saat.</summary>
public sealed class SettableClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// Her test için gerçek sunucu (bellek içi HTTP) + geçici klasörde gerçek SQLite dosyası.
/// Gerçek veri klasörüne (<c>%ProgramData%</c>) hiçbir şey yazılmaz.
/// </summary>
public sealed class ServerFixture : WebApplicationFactory<Program>
{
    static ServerFixture() => KumasBarkod.Server.Auth.AuthService.Iterations = 1_000;   // testler hızlı (gerçek sunucu 100.000)

    public string DataDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "kumas-server-tests", Guid.NewGuid().ToString("N"));

    public SettableClock Clock { get; } = new();

    /// <summary>Google Apps Script yerine (internete çıkılmaz).</summary>
    public FakeAppsScript Sheets { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(new ServerPaths(DataDirectory));
            services.AddSingleton<TimeProvider>(Clock);
            // Arka plan işleri (yedek, Sheets) testte kendiliğinden çalışmasın; testler doğrudan çağırır.
            services.RemoveAll<IHostedService>();
            services.AddSingleton(new AppsScriptTransport(new HttpClient(Sheets)));
        });
    }

    public SqliteConnectionFactory Db => Services.GetRequiredService<SqliteConnectionFactory>();

    public SqliteConnection Open() => Db.Open();

    public async Task<HttpResponseMessage> PostBatch(CutsBatchRequest request, HttpClient? client = null) =>
        await (client ?? CreateClient()).PostAsJsonAsync("/" + ServerApiContract.CutsBatchPath, request, ServerApiContract.Json);

    public async Task<CutsBatchResponse> PostOk(CutsBatchRequest request)
    {
        using var response = await PostBatch(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return System.Text.Json.JsonSerializer.Deserialize<CutsBatchResponse>(text, ServerApiContract.Json)!;
    }

    public async Task<ServerError> PostError(CutsBatchRequest request, int expectedStatus)
    {
        using var response = await PostBatch(request);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ServerError>(ServerApiContract.Json))!;
    }

    public IReadOnlyList<string> Invariants() => ServerInvariants.Check(Db);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Test satırı üretici.</summary>
public static class Rows
{
    public static CutSyncRow Cut(string station, long seq, string lot = "LOT-3", long cm = 2550,
        string created = "2026-11-15T08:00:00.000Z", long printCount = 1, string? lastPrinted = "2026-11-15T08:00:01.000Z",
        string status = "active", string? voidReason = null, string? voidedAt = null) =>
        new($"{station}-{seq:D6}", station, seq, lot, cm, null, status, voidReason, voidedAt, created, printCount, lastPrinted);

    public static CutSyncRow Voided(CutSyncRow r, string reason = "Çift kayıt", string at = "2026-11-15T09:00:00.000Z") =>
        r with { Status = "voided", VoidReason = reason, VoidedAt = at };

    public static CutsBatchRequest Batch(string installId, string station, params CutSyncRow[] rows) =>
        new(installId, station, "1.1.0", rows);
}
