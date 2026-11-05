using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KumasBarkod.Core.Sync;

/// <summary>
/// Google Apps Script web uygulamasına HTTPS POST. Gövde: <c>{"key": "...", "rows": [...]}</c>,
/// alan adları snake_case (tablo sütunlarıyla aynı). Yanıt: <c>{"ok": true}</c> ya da
/// <c>{"ok": false, "error": "..."}</c>. Apps Script POST'a 302 ile yanıt verir; HttpClient
/// yönlendirmeyi GET ile izler (varsayılan davranış).
/// Geri kurma: <c>{"key", "action": "export", "offset", "limit"}</c> → <c>{"ok", "total", "rows"}</c>.
/// </summary>
public sealed class AppsScriptTransport(HttpClient http) : ISyncTransport
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Tabloda sayı metin olarak durursa ("2550") da okunabilsin
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Gönderim için gereken en düşük Apps Script sürümü (v3: ID çakışma koruması).</summary>
    public const int RequiredScriptVersion = 3;

    public async Task<SendResult> SendAsync(SyncTarget target, IReadOnlyList<CutSyncRow> rows, CancellationToken cancellationToken)
    {
        var reply = await PostAsync<Reply>(target, new Payload(target.Key, rows), cancellationToken);
        if (reply.Version < RequiredScriptVersion)
            throw new SyncException(SyncFailureKind.Rejected,
                $"Apps Script eski sürüm (v{reply.Version}). Aynı ID'li kayıtlar birbirinin üzerine yazılmasın diye " +
                $"v{RequiredScriptVersion} gerekir: Code.gs'yi güncelleyip yeniden dağıtın (KURULUM.md).");
        return new SendResult(reply.Conflicts ?? [], reply.MaxSequences ?? new Dictionary<string, long>());
    }

    public async Task<ExportPage> ExportAsync(SyncTarget target, int offset, int limit, CancellationToken cancellationToken)
    {
        var reply = await PostAsync<ExportReply>(target, new ExportRequest(target.Key, "export", offset, limit), cancellationToken);
        if (reply.Rows is null)
            throw new SyncException(SyncFailureKind.Rejected,
                "Tablo okuma desteklenmiyor. Apps Script'i son sürümle güncelleyip yeniden dağıtın (KURULUM.md).");
        return new ExportPage(reply.Total, reply.Rows);
    }

    private async Task<T> PostAsync<T>(SyncTarget target, object body, CancellationToken cancellationToken)
        where T : class, IReply
    {
        if (!Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new SyncException(SyncFailureKind.Rejected, "Sheets adresi geçerli bir https adresi değil.");

        HttpResponseMessage response;
        try
        {
            using var content = JsonContent.Create(body, body.GetType(), options: Json);
            response = await http.PostAsync(uri, content, cancellationToken);
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
            T? reply = null;
            try { reply = JsonSerializer.Deserialize<T>(text, Json); }
            catch (JsonException ex) when (text.TrimStart().StartsWith('{'))
            {
                // JSON geldi ama bir değerin tipi uymuyor: erişim sorunu değil, tablodaki veri sorunu.
                throw new SyncException(SyncFailureKind.Rejected,
                    $"Sheets yanıtındaki bir değer okunamadı ({ex.Path}). Tabloda elle değiştirilmiş hücre olabilir; Apps Script'i son sürüme güncelleyin.", ex);
            }
            catch (JsonException) { }

            if (reply is null)
            {
                // Genelde bir Google giriş sayfası (HTML) döner: web uygulaması "Herkes" erişimine açık değil.
                throw new SyncException(SyncFailureKind.Rejected,
                    "Sheets yanıtı anlaşılamadı. Apps Script'in 'Herkes' erişimiyle yayınlandığını kontrol edin.");
            }
            if (!reply.Ok)
                throw new SyncException(SyncFailureKind.Rejected, "Sheets reddetti: " + (reply.Error ?? "bilinmeyen hata"));
            return reply;
        }
    }

    private interface IReply
    {
        bool Ok { get; }
        string? Error { get; }
    }

    private sealed record Payload(string Key, IReadOnlyList<CutSyncRow> Rows);

    private sealed record ExportRequest(string Key, string Action, int Offset, int Limit);

    private sealed record Reply(
        bool Ok, string? Error, int Version,
        List<SyncConflict>? Conflicts, Dictionary<string, long>? MaxSequences) : IReply;

    private sealed record ExportReply(bool Ok, string? Error, int Total, List<CutSyncRow>? Rows) : IReply;
}
