using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KumasBarkod.Core.Sync;

namespace KumasBarkod.Core.ServerApi;

/// <summary>Sunucu adresi: "depo-sunucu", "192.168.1.20:5080" ya da tam "http://…" yazılabilir.</summary>
public static class ServerAddress
{
    /// <summary>Yazılanı tam adrese çevirir (şema yoksa http, kapı yoksa 5080, sonda "/").</summary>
    public static bool TryNormalize(string? input, out Uri? baseUri, out string? error)
    {
        baseUri = null;
        error = null;
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "Sunucu adresi boş.";
            return false;
        }

        var hasScheme = text.Contains("://", StringComparison.Ordinal);
        if (!hasScheme) text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host)
            || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "Sunucu adresi geçersiz. Örnek: depo-sunucu ya da 192.168.1.20:5080";
            return false;
        }

        var builder = new UriBuilder(uri);
        if (!hasScheme && uri.IsDefaultPort) builder.Port = ServerApiContract.DefaultPort;
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        baseUri = builder.Uri;
        return true;
    }
}

/// <summary>Depo sunucusuna gönderim.</summary>
public interface IServerTransport
{
    Task<CutsBatchResponse> SendCutsAsync(Uri baseUri, CutsBatchRequest request, CancellationToken cancellationToken);

    Task<HealthResponse> HealthAsync(Uri baseUri, CancellationToken cancellationToken);
}

/// <summary>
/// HTTP üzerinden JSON. Ulaşılamayan sunucu, zaman aşımı, 5xx → <see cref="SyncFailureKind.Offline"/>
/// (kendiliğinden düzelir); 4xx ve anlaşılmayan yanıt → <see cref="SyncFailureKind.Rejected"/>.
/// </summary>
public sealed class HttpServerTransport(HttpClient http) : IServerTransport
{
    public async Task<CutsBatchResponse> SendCutsAsync(Uri baseUri, CutsBatchRequest request, CancellationToken cancellationToken)
    {
        var reply = await SendAsync<CutsBatchResponse>(
            () => new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, ServerApiContract.CutsBatchPath))
            {
                Content = JsonContent.Create(request, options: ServerApiContract.Json),
            }, cancellationToken);
        CheckVersion(reply.ApiVersion);
        return reply with
        {
            Conflicts = reply.Conflicts ?? [],
            MaxSequences = reply.MaxSequences ?? new Dictionary<string, long>(),
        };
    }

    public async Task<HealthResponse> HealthAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        var reply = await SendAsync<HealthResponse>(
            () => new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, ServerApiContract.HealthPath)), cancellationToken);
        if (reply.Service != "KumasBarkod.Server")
            throw new SyncException(SyncFailureKind.Rejected, "Bu adreste Kumaş Barkod sunucusu yok.");
        CheckVersion(reply.ApiVersion);
        return reply;
    }

    private static void CheckVersion(int serverApiVersion)
    {
        if (serverApiVersion < ServerApiContract.ApiVersion)
            throw new SyncException(SyncFailureKind.Rejected,
                $"Depo sunucusu eski sürüm (API v{serverApiVersion}). Sunucuyu güncelleyin; kayıtlar bekliyor.");
    }

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> create, CancellationToken cancellationToken) where T : class
    {
        HttpResponseMessage response;
        try
        {
            using var request = create();
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncException(SyncFailureKind.Offline, "Depo sunucusuna ulaşılamıyor (ağ kablosu/Wi-Fi ya da sunucu kapalı olabilir).", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SyncException(SyncFailureKind.Offline, "Depo sunucusu yanıt vermedi (zaman aşımı).", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var message = TryRead<ServerError>(text)?.Message;
                if ((int)response.StatusCode >= 500)
                    throw new SyncException(SyncFailureKind.Offline,
                        $"Depo sunucusu geçici hata verdi ({(int)response.StatusCode}){(message is null ? "" : ": " + message)}.");
                if (response.StatusCode == HttpStatusCode.NotFound && message is null)
                    throw new SyncException(SyncFailureKind.Rejected, "Bu adreste Kumaş Barkod sunucusu yok (404).");
                throw new SyncException(SyncFailureKind.Rejected,
                    message ?? $"Depo sunucusu isteği reddetti ({(int)response.StatusCode}).");
            }

            return TryRead<T>(text)
                ?? throw new SyncException(SyncFailureKind.Rejected, "Depo sunucusunun yanıtı anlaşılamadı. Adres doğru mu?");
        }
    }

    private static T? TryRead<T>(string text) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(text, ServerApiContract.Json); }
        catch (JsonException) { return null; }
    }
}
