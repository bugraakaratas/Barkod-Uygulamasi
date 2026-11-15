using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Sync;

namespace KumasBarkod.Core.ServerApi;

/// <summary>
/// Depo ekranlarının (stok, lotlar; Faz 4'te sevkiyat) sunucu istemcisi. Kural yok: her karar sunucuda.
/// Hata: <see cref="SyncException"/> (Offline = ulaşılamıyor, Rejected = sunucu reddetti, Türkçe mesaj).
/// Adres her çağrıda ayarlardan okunur (Ayarlar'da değişince hemen geçerli).
/// </summary>
public sealed class DepotApiClient(HttpClient http, SettingsRepository settings, string deviceName)
{
    public bool IsConfigured => settings.GetServerBaseUri() is not null;

    public Task<DataVersionResponse> GetVersionAsync(CancellationToken ct = default) =>
        Get<DataVersionResponse>(DepotPaths.Version, ct);

    public Task<StockResponse> GetStockAsync(string state, long? lotId, bool unmatched, string? search, int offset, int limit,
        CancellationToken ct = default)
    {
        var q = new StringBuilder(DepotPaths.Stock).Append("?state=").Append(Uri.EscapeDataString(state));
        if (lotId is { } id) q.Append("&lotId=").Append(id.ToString(CultureInfo.InvariantCulture));
        if (unmatched) q.Append("&unmatched=true");
        if (!string.IsNullOrWhiteSpace(search)) q.Append("&search=").Append(Uri.EscapeDataString(search.Trim()));
        q.Append("&offset=").Append(offset.ToString(CultureInfo.InvariantCulture))
         .Append("&limit=").Append(limit.ToString(CultureInfo.InvariantCulture));
        return Get<StockResponse>(q.ToString(), ct);
    }

    public Task<RollDetail> GetRollAsync(string code, CancellationToken ct = default) =>
        Get<RollDetail>(DepotPaths.Rolls + "/" + Uri.EscapeDataString(code), ct);

    public Task<List<LotInfo>> GetLotsAsync(CancellationToken ct = default) => Get<List<LotInfo>>(DepotPaths.Lots, ct);

    public Task<List<UnmatchedLot>> GetUnmatchedAsync(CancellationToken ct = default) =>
        Get<List<UnmatchedLot>>(DepotPaths.UnmatchedLots, ct);

    public Task<LotInfo> CreateLotAsync(string name, CancellationToken ct = default) =>
        Send<LotInfo>(HttpMethod.Post, DepotPaths.Lots, new LotCreateRequest(name), ct);

    public Task<LotInfo> UpdateLotAsync(long lotId, string? name, bool? active, CancellationToken ct = default) =>
        Send<LotInfo>(HttpMethod.Put, $"{DepotPaths.Lots}/{lotId.ToString(CultureInfo.InvariantCulture)}",
            new LotUpdateRequest(name, active), ct);

    public Task<LotAliasResponse> MapRawTextAsync(long lotId, string rawText, CancellationToken ct = default) =>
        Send<LotAliasResponse>(HttpMethod.Post, $"{DepotPaths.Lots}/{lotId.ToString(CultureInfo.InvariantCulture)}/aliases",
            new LotAliasRequest(rawText), ct);

    private Task<T> Get<T>(string path, CancellationToken ct) where T : class =>
        ServerHttp.SendAsync<T>(http, () => Request(HttpMethod.Get, path), ct);

    private Task<T> Send<T>(HttpMethod method, string path, object body, CancellationToken ct) where T : class =>
        ServerHttp.SendAsync<T>(http, () =>
        {
            var request = Request(method, path);
            request.Content = JsonContent.Create(body, body.GetType(), options: ServerApiContract.Json);
            return request;
        }, ct);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var baseUri = settings.GetServerBaseUri()
            ?? throw new SyncException(SyncFailureKind.Rejected, "Depo sunucusu ayarlanmadı (Ayarlar → Depo sunucusu).");
        var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.TryAddWithoutValidation(DepotPaths.DeviceHeader, deviceName);
        return request;
    }
}
