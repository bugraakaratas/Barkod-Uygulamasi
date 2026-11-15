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

    // ---------- Oturum (bilgisayar başına bir giriş; Sevkiyat ve Personel ekranları ortak kullanır) ----------

    private LoginResponse? _session;

    public UserInfo? CurrentUser => _session?.User;

    /// <summary>Giriş/çıkış oldu ya da sunucu oturumu reddetti (süre doldu, personel pasif).</summary>
    public event Action? SessionChanged;

    public async Task<UserInfo> LoginAsync(string badge, string pin, CancellationToken ct = default)
    {
        _session = await Send<LoginResponse>(HttpMethod.Post, ShipmentPaths.Login, new LoginRequest(badge, pin), ct);
        SessionChanged?.Invoke();
        return _session.User;
    }

    public async Task LogoutAsync()
    {
        if (_session is null) return;
        try { await Send<object>(HttpMethod.Post, ShipmentPaths.Logout, new { }, default); }
        catch (SyncException) { /* sunucuya ulaşılamasa da yerelde çıkış yapılır */ }
        _session = null;
        SessionChanged?.Invoke();
    }

    public Task<BootstrapNeededResponse> BootstrapNeededAsync(CancellationToken ct = default) =>
        Get<BootstrapNeededResponse>(ShipmentPaths.BootstrapNeeded, ct);

    public Task<List<UserInfo>> GetUsersAsync(CancellationToken ct = default) => Get<List<UserInfo>>(ShipmentPaths.Users, ct);

    public Task<UserInfo> CreateUserAsync(UserCreateRequest request, CancellationToken ct = default) =>
        Send<UserInfo>(HttpMethod.Post, ShipmentPaths.Users, request, ct);

    public Task<UserInfo> UpdateUserAsync(long userId, UserUpdateRequest request, CancellationToken ct = default) =>
        Send<UserInfo>(HttpMethod.Put, $"{ShipmentPaths.Users}/{userId.ToString(CultureInfo.InvariantCulture)}", request, ct);

    // ---------- Sevkiyat ----------

    public Task<List<ShipmentSummary>> GetShipmentsAsync(bool open, CancellationToken ct = default) =>
        Get<List<ShipmentSummary>>(ShipmentPaths.Shipments + (open ? "?status=open" : "?status=closed"), ct);

    public Task<ShipmentDetail> GetShipmentAsync(long id, CancellationToken ct = default) =>
        Get<ShipmentDetail>($"{ShipmentPaths.Shipments}/{id.ToString(CultureInfo.InvariantCulture)}", ct);

    public Task<ShipmentDetail> OpenShipmentAsync(IReadOnlyList<long> lotIds, CancellationToken ct = default) =>
        Send<ShipmentDetail>(HttpMethod.Post, ShipmentPaths.Shipments, new ShipmentOpenRequest(lotIds), ct);

    public Task<ScanResponse> ScanAsync(long shipmentId, ScanRequest request, bool remove, CancellationToken ct = default) =>
        Send<ScanResponse>(HttpMethod.Post,
            $"{ShipmentPaths.Shipments}/{shipmentId.ToString(CultureInfo.InvariantCulture)}/{(remove ? "removals" : "scans")}", request, ct);

    public Task<ShipmentDetail> ConfirmShipmentAsync(long shipmentId, long expectedVersion, string requestId, CancellationToken ct = default) =>
        Send<ShipmentDetail>(HttpMethod.Post, $"{ShipmentPaths.Shipments}/{shipmentId.ToString(CultureInfo.InvariantCulture)}/confirm",
            new ShipmentConfirmRequest(expectedVersion, requestId), ct);

    public Task<ShipmentDetail> CancelShipmentAsync(long shipmentId, string reason, string requestId, CancellationToken ct = default) =>
        Send<ShipmentDetail>(HttpMethod.Post, $"{ShipmentPaths.Shipments}/{shipmentId.ToString(CultureInfo.InvariantCulture)}/cancel",
            new ShipmentCancelRequest(reason, requestId), ct);

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

    public Task<ShipmentDetail> ReverseShipmentAsync(long shipmentId, string reason, string requestId, CancellationToken ct = default) =>
        Send<ShipmentDetail>(HttpMethod.Post, $"{ShipmentPaths.Shipments}/{shipmentId.ToString(CultureInfo.InvariantCulture)}/reverse",
            new ShipmentReverseRequest(reason, requestId), ct);

    // ---------- İstisnalar (yetkili) ve raporlar ----------

    public Task<ExceptionsResponse> GetExceptionsAsync(CancellationToken ct = default) => Get<ExceptionsResponse>(ReportPaths.Exceptions, ct);

    public Task<ExceptionsResponse> ResolveConflictAsync(long conflictId, string note, CancellationToken ct = default) =>
        Send<ExceptionsResponse>(HttpMethod.Post, $"{ReportPaths.ResolveConflict}/{conflictId.ToString(CultureInfo.InvariantCulture)}/resolve",
            new ConflictResolveRequest(note), ct);

    public Task<List<StockByLotRow>> GetStockByLotAsync(CancellationToken ct = default) => Get<List<StockByLotRow>>(ReportPaths.StockByLot, ct);

    public Task<List<DailyShipmentRow>> GetDailyShipmentsAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Get<List<DailyShipmentRow>>(ReportPaths.DailyShipments + RangeQuery(from, to), ct);

    public Task<List<ShippedRollRow>> GetShippedRollsAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Get<List<ShippedRollRow>>(ReportPaths.ShippedRolls + RangeQuery(from, to), ct);

    public Task<List<ShipmentHistoryRow>> GetShipmentHistoryAsync(DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Get<List<ShipmentHistoryRow>>(ReportPaths.ShipmentHistory + RangeQuery(from, to), ct);

    private static string RangeQuery(DateOnly from, DateOnly to) =>
        $"?from={from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}&to={to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    private Task<T> Get<T>(string path, CancellationToken ct) where T : class =>
        Guard(ServerHttp.SendAsync<T>(http, () => Request(HttpMethod.Get, path), ct));

    private Task<T> Send<T>(HttpMethod method, string path, object body, CancellationToken ct) where T : class =>
        Guard(ServerHttp.SendAsync<T>(http, () =>
        {
            var request = Request(method, path);
            request.Content = JsonContent.Create(body, body.GetType(), options: ServerApiContract.Json);
            return request;
        }, ct));

    /// <summary>Sunucu oturumu reddederse (401) yerel oturum da kapanır: ekran giriş ister.</summary>
    private async Task<T> Guard<T>(Task<T> call)
    {
        try
        {
            return await call;
        }
        catch (SyncException ex) when (ex.StatusCode == 401 && _session is not null)
        {
            _session = null;
            SessionChanged?.Invoke();
            throw;
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var baseUri = settings.GetServerBaseUri()
            ?? throw new SyncException(SyncFailureKind.Rejected, "Depo sunucusu ayarlanmadı (Ayarlar → Depo sunucusu).");
        var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.TryAddWithoutValidation(DepotPaths.DeviceHeader, deviceName);
        if (_session is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session.Token);
        return request;
    }
}
