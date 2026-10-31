using KumasBarkod.Core.Data;

namespace KumasBarkod.Core.Sync;

/// <summary>
/// Bir gönderim turu: kuyruktaki kesimleri 200'lük paketlerle gönderir. Bir paket başarısız
/// olursa tur durur (sıra korunur), kuyruk satırları bekler ve sonra tekrar denenir.
/// Kayıt, baskı ve okutma bu servise hiç bağlı değildir: internet yokken uygulama aynen çalışır.
/// </summary>
public sealed class SyncService(
    OutboxRepository outbox,
    SettingsRepository settings,
    ISyncTransport transport,
    TimeProvider clock)
{
    public const int BatchSize = 200;
    public const int MaxBatchesPerRun = 25;          // bir turda en fazla 5.000 kesim
    public static readonly TimeSpan KeepSentFor = TimeSpan.FromDays(30);

    public async Task<SyncRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var target = settings.GetSyncTarget();
        if (target is null) return new SyncRunResult(false, 0, null);

        var sent = 0;
        for (var i = 0; i < MaxBatchesPerRun; i++)
        {
            var batch = outbox.GetPendingBatch(BatchSize);
            if (batch.Count == 0) break;

            try
            {
                await transport.SendAsync(target, batch.Select(p => p.Row).ToList(), cancellationToken);
            }
            catch (SyncException ex)
            {
                outbox.MarkFailed(batch, ex.Message);
                return new SyncRunResult(true, sent, ex);
            }

            outbox.MarkSent(batch, clock.GetUtcNow());
            sent += batch.Count;
        }

        outbox.PurgeSent(clock.GetUtcNow() - KeepSentFor);
        return new SyncRunResult(true, sent, null);
    }

    /// <summary>Ayarlar ekranındaki "Bağlantıyı test et": boş paket gönderir (anahtar + tablo kontrolü).</summary>
    public Task TestConnectionAsync(SyncTarget target, CancellationToken cancellationToken = default) =>
        transport.SendAsync(target, [], cancellationToken);
}
