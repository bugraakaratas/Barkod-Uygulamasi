using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Server.Data;
using KumasBarkod.Server.Ingest;
using KumasBarkod.Server.Lots;
using KumasBarkod.Server.Stock;

namespace KumasBarkod.Server;

/// <summary>
/// HTTP uç noktaları. Bütün iş kuralları serviste; burada yalnızca istek/yanıt eşlemesi var.
/// Aynı uç noktaları WPF istasyonu, rıhtım ekranı ve ileride el terminali kullanır.
/// </summary>
public static class ServerEndpoints
{
    public static void MapServerEndpoints(this WebApplication app)
    {
        app.MapGet("/" + ServerApiContract.HealthPath, (SqliteConnectionFactory db, TimeProvider clock) =>
        {
            using var c = db.Open();
            return new HealthResponse(true, ServerInfo.ServiceId, ServerApiContract.ApiVersion, ServerInfo.Version,
                DatabaseMigrator.GetSchemaVersion(c), Timestamps.Format(clock.GetUtcNow()));
        });

        app.MapPost("/" + ServerApiContract.CutsBatchPath, (CutsBatchRequest? request, CutIngestService ingest) =>
            request is null
                ? throw new ApiException(400, ServerErrorCodes.InvalidRequest, "İstek gövdesi boş.")
                : ingest.Ingest(request));

        // ---------- Depo: veri sürümü, stok, top, lotlar ----------

        app.MapGet("/" + DepotPaths.Version, (SqliteConnectionFactory db) =>
        {
            using var c = db.Open();
            return new DataVersionResponse(DataVersion.Get(c));
        });

        app.MapGet("/" + DepotPaths.Stock, (StockQueryService stock, string? state, long? lotId, bool? unmatched,
                string? search, int? offset, int? limit) =>
            stock.Query(new StockQuery(state ?? RollStateNames.InStock, lotId, unmatched ?? false, search,
                offset ?? 0, limit ?? 200)));

        app.MapGet("/" + DepotPaths.Rolls + "/{code}", (string code, StockQueryService stock) =>
            stock.Find(code) is { } roll
                ? Results.Ok(roll)
                : throw new ApiException(404, "ROLL_NOT_FOUND", $"{code}: sunucuda böyle bir top yok."));

        app.MapGet("/" + DepotPaths.Lots, (LotService lots) => lots.List());
        app.MapGet("/" + DepotPaths.UnmatchedLots, (LotService lots) => lots.Unmatched());
        app.MapPost("/" + DepotPaths.Lots, (LotCreateRequest? request, LotService lots, HttpContext http) =>
            lots.Create(request?.Name, Actor(http)));
        app.MapPut("/" + DepotPaths.Lots + "/{lotId:long}", (long lotId, LotUpdateRequest? request, LotService lots, HttpContext http) =>
            lots.Update(lotId, request ?? new LotUpdateRequest(null, null), Actor(http)));
        app.MapPost("/" + DepotPaths.Lots + "/{lotId:long}/aliases", (long lotId, LotAliasRequest? request, LotService lots, HttpContext http) =>
            lots.MapRawText(lotId, request?.RawText, Actor(http)));
    }

    /// <summary>İşlemi yapan cihaz (kullanıcı girişi Faz 4'te). En fazla 100 karakter.</summary>
    private static string? Actor(HttpContext http)
    {
        var device = http.Request.Headers[DepotPaths.DeviceHeader].ToString().Trim();
        return device.Length == 0 ? null : device.Length > 100 ? device[..100] : device;
    }
}

/// <summary>
/// Merkezi hata yönetimi: <see cref="ApiException"/> → kendi durum kodu; bozuk JSON → 400;
/// beklenmeyen hata → 500 + günlük. Yanıt her zaman <see cref="ServerError"/> (Türkçe mesaj).
/// </summary>
public sealed class ApiErrorMiddleware(RequestDelegate next, ILogger<ApiErrorMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException ex)
        {
            log.LogWarning("İstek reddedildi {Path}: {Code} {Message}", context.Request.Path, ex.Code, ex.Message);
            await Write(context, ex.StatusCode, ex.Code, ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            log.LogWarning("Bozuk istek {Path}: {Message}", context.Request.Path, ex.Message);
            await Write(context, 400, ServerErrorCodes.InvalidRequest, "İstek okunamadı (bozuk veya eksik veri).");
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            log.LogError(ex, "Beklenmeyen hata {Path}", context.Request.Path);
            await Write(context, 500, ServerErrorCodes.Internal, "Sunucuda beklenmeyen hata oldu ve günlüğe yazıldı. Tekrar deneyin.");
        }
    }

    private static async Task Write(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ServerError(false, code, message), ServerApiContract.Json);
    }
}
