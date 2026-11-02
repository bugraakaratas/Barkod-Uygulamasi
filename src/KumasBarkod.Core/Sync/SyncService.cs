using System.Globalization;
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
    StationCounters counters,
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
            if (batch.Count == 0)
            {
                // Bu tabloya ilk kez bağlanılıyorsa sayaçları tablodan öğren (yeni kurulum, tabloda
                // zaten bulunan ID'leri tekrar üretmesin) — gönderilecek kayıt olmasa da.
                if (i == 0 && settings.Get(SettingKeys.SheetsCountersCheckedUrl) != target.Url)
                {
                    try
                    {
                        Learn(target, await transport.SendAsync(target, [], cancellationToken));
                    }
                    catch (SyncException ex)
                    {
                        return new SyncRunResult(true, 0, ex);
                    }
                }
                break;
            }

            SendResult result;
            try
            {
                result = await transport.SendAsync(target, batch.Select(p => p.Row).ToList(), cancellationToken);
            }
            catch (SyncException ex)
            {
                outbox.MarkFailed(batch, ex.Message);
                return new SyncRunResult(true, sent, ex);
            }

            Learn(target, result);

            var conflicted = result.Conflicts.Select(c => c.CutId).ToHashSet(StringComparer.Ordinal);
            var accepted = batch.Where(p => !conflicted.Contains(p.Row.CutId)).ToList();
            outbox.MarkSent(accepted, clock.GetUtcNow());
            sent += accepted.Count;

            if (conflicted.Count > 0)
            {
                // Çakışan kesimler kuyrukta kalır (veri kaybolmaz); tur durur ve hata görünür.
                var failure = new SyncException(SyncFailureKind.Rejected, ConflictMessage(result.Conflicts));
                outbox.MarkFailed(batch.Where(p => conflicted.Contains(p.Row.CutId)).ToList(), failure.Message);
                return new SyncRunResult(true, sent, failure);
            }
        }

        outbox.PurgeSent(clock.GetUtcNow() - KeepSentFor);
        return new SyncRunResult(true, sent, null);
    }

    private void Learn(SyncTarget target, SendResult result)
    {
        counters.RaiseTo(result.MaxSequences);
        settings.Set(SettingKeys.SheetsCountersCheckedUrl, target.Url);
    }

    public static string ConflictMessage(IReadOnlyList<SyncConflict> conflicts)
    {
        var first = conflicts[0];
        var more = conflicts.Count > 1 ? $" (ve {conflicts.Count - 1} kayıt daha)" : "";
        return $"{first.CutId}{more} gönderilmedi: bu ID Sheets'te başka bir kesime ait " +
               $"({FormatLocal(first.ExistingCreatedAt)}), tablodaki kayıt korundu. Aynı istasyon kodu başka " +
               "bir bilgisayarda/veritabanında kullanılıyor olabilir; Ayarlar'dan farklı bir istasyon kodu verin.";
    }

    private static string FormatLocal(string utc) =>
        DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("tr-TR"))
            : utc;

    /// <summary>Ayarlar ekranındaki "Bağlantıyı test et": boş paket gönderir (anahtar + tablo kontrolü).</summary>
    public Task TestConnectionAsync(SyncTarget target, CancellationToken cancellationToken = default) =>
        transport.SendAsync(target, [], cancellationToken);
}
