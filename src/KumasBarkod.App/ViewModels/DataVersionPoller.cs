using System.Windows.Threading;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;

namespace KumasBarkod.App.ViewModels;

/// <summary>
/// Ekran görünürken sunucuya birkaç saniyede bir "veri değişti mi?" (<c>GET /version</c>, küçük yanıt) sorar;
/// değiştiyse <see cref="Changed"/> tetiklenir ve ekran yeniden okur. Böylece aynı anda açık bütün ekranlar
/// (rıhtım, patron) en geç <see cref="Interval"/> içinde güncellenir. Ekran görünmezken sormaz.
/// </summary>
public sealed class DataVersionPoller
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private readonly DepotApiClient _api;
    private readonly DispatcherTimer _timer;
    private long? _lastVersion;
    private bool _busy;

    public DataVersionPoller(DepotApiClient api)
    {
        _api = api;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    /// <summary>Veri değişti (ya da ilk okuma): ekran yeniden okusun.</summary>
    public event Func<Task>? Changed;

    /// <summary>Sunucuya ulaşılamadı (mesaj) ya da yeniden ulaşıldı (null).</summary>
    public event Action<string?>? ConnectionChanged;

    public DateTimeOffset? LastSuccess { get; private set; }

    public void Start()
    {
        _lastVersion = null;   // ekran her açıldığında bir kez okunur
        _timer.Start();
        _ = TickAsync();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Kullanıcı bir değişiklik yaptıktan sonra hemen yenile.</summary>
    public void Invalidate() => _lastVersion = null;

    private async Task TickAsync()
    {
        if (_busy || !_api.IsConfigured) return;
        _busy = true;
        try
        {
            var version = (await _api.GetVersionAsync()).DataVersion;
            LastSuccess = DateTimeOffset.Now;
            ConnectionChanged?.Invoke(null);
            if (version != _lastVersion && Changed is { } changed)
            {
                await changed();
                _lastVersion = version;
            }
        }
        catch (SyncException ex)
        {
            ConnectionChanged?.Invoke(ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }
}
