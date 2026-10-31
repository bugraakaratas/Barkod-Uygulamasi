namespace KumasBarkod.Core.Sync;

/// <summary>
/// Arka plan göndericisi. Normalde 2 dakikada bir, yeni kayıttan ~5 sn sonra da gönderir.
/// Hata olursa bekleme artar (30 sn → 1 → 2 → 5 → 10 dk); bekleme sırasında yeni kayıt
/// gelse de erken denemez (internet yokken her kayıtta boşuna denemesin).
/// </summary>
public sealed class SyncWorker : IAsyncDisposable
{
    public static readonly TimeSpan NormalInterval = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan NudgeDelay = TimeSpan.FromSeconds(5);

    private readonly SyncService _service;
    private readonly OutboxRepository _outbox;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _failures;
    private DateTimeOffset? _lastSuccess;

    public SyncWorker(SyncService service, OutboxRepository outbox, TimeProvider clock)
    {
        _service = service;
        _outbox = outbox;
        _clock = clock;
        Status = new SyncStatus(SyncState.Idle, 0, null, null, null);
    }

    /// <summary>Arka plan iş parçacığından çağrılır; arayüz kendi iş parçacığına aktarmalı.</summary>
    public event Action<SyncStatus>? StatusChanged;

    public SyncStatus Status { get; private set; }

    /// <summary>Hata sayısına göre sonraki denemeye kadar bekleme.</summary>
    public static TimeSpan Backoff(int failures) => failures switch
    {
        <= 0 => NormalInterval,
        1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(1),
        3 => TimeSpan.FromMinutes(2),
        4 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(10),
    };

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>Yeni kayıt/değişiklik oldu: hata beklemesinde değilsek kısa süre sonra gönder.</summary>
    public void Nudge()
    {
        if (_failures == 0) _signal.Release();
    }

    /// <summary>"Şimdi gönder": hata beklemesini de atlar.</summary>
    public void SendNow()
    {
        _failures = 0;
        _signal.Release();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        await RunAsync(ct);   // açılışta hemen bir tur
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var woken = await _signal.WaitAsync(Backoff(_failures), ct);
                if (woken)
                {
                    await Task.Delay(NudgeDelay, ct);       // art arda kayıtları tek pakette topla
                    while (_signal.CurrentCount > 0) _signal.Wait(0);
                }
                await RunAsync(ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Publish(SyncState.Sending, null, null);
        SyncRunResult result;
        try
        {
            result = await _service.RunOnceAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Beklenmeyen hata (ör. veritabanı kilidi): gönderici durmasın.
            result = new SyncRunResult(true, 0, new SyncException(SyncFailureKind.Rejected, ex.Message, ex));
        }

        if (!result.Configured)
        {
            _failures = 0;
            Publish(SyncState.NotConfigured, null, null);
        }
        else if (result.Failure is { } failure)
        {
            _failures++;
            Publish(failure.Kind == SyncFailureKind.Offline ? SyncState.Offline : SyncState.Error,
                failure.Message, _clock.GetUtcNow() + Backoff(_failures));
        }
        else
        {
            _failures = 0;
            _lastSuccess = _clock.GetUtcNow();
            Publish(SyncState.Idle, null, null);
        }
    }

    private void Publish(SyncState state, string? error, DateTimeOffset? next)
    {
        long pending;
        try { pending = _outbox.CountPendingCuts(); }
        catch { pending = Status.PendingCount; }

        Status = new SyncStatus(state, pending, _lastSuccess, error, next);
        StatusChanged?.Invoke(Status);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
        _signal.Dispose();
    }
}
