using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KumasBarkod.Core.Sync;

/// <summary>
/// Google Apps Script web uygulamasına HTTPS POST. Gövde: <c>{"key": "...", "rows": [...]}</c>,
/// alan adları snake_case (tablo sütunlarıyla aynı). Yanıt: <c>{"ok": true}</c> ya da
/// <c>{"ok": false, "error": "..."}</c>. Apps Script POST'a 302 ile yanıt verir; HttpClient
/// yönlendirmeyi GET ile izler (varsayılan davranış).
/// </summary>
public sealed class AppsScriptTransport(HttpClient http) : ISyncTransport
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public async Task SendAsync(SyncTarget target, IReadOnlyList<CutSyncRow> rows, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new SyncException(SyncFailureKind.Rejected, "Sheets adresi geçerli bir https adresi değil.");

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(uri, new Payload(target.Key, rows), Json, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncException(SyncFailureKind.Offline, "Google'a ulaşılamıyor (internet yok olabilir).", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SyncException(SyncFailureKind.Offline, "Google yanıt vermedi (zaman aşımı).", ex);
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500)
                throw new SyncException(SyncFailureKind.Offline, $"Google geçici hata verdi ({(int)response.StatusCode}).");

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            Reply? reply = null;
            try { reply = JsonSerializer.Deserialize<Reply>(text, Json); }
            catch (JsonException) { }

            if (reply is null)
            {
                // Genelde bir Google giriş sayfası (HTML) döner: web uygulaması "Herkes" erişimine açık değil.
                throw new SyncException(SyncFailureKind.Rejected,
                    "Sheets yanıtı anlaşılamadı. Apps Script'in 'Herkes' erişimiyle yayınlandığını kontrol edin.");
            }
            if (!reply.Ok)
                throw new SyncException(SyncFailureKind.Rejected, "Sheets reddetti: " + (reply.Error ?? "bilinmeyen hata"));
        }
    }

    private sealed record Payload(string Key, IReadOnlyList<CutSyncRow> Rows);

    private sealed record Reply(bool Ok, string? Error);
}
