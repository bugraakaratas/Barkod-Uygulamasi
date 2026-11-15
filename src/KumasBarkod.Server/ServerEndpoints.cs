using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Server.Data;
using KumasBarkod.Server.Ingest;

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
