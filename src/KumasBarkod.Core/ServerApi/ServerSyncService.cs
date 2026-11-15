using KumasBarkod.Core.Data;
using KumasBarkod.Core.Sync;

namespace KumasBarkod.Core.ServerApi;

/// <summary>Bu bilgisayarın sunucuya kendini tanıttığı bilgiler.</summary>
/// <param name="InstallId">Kalıcı bilgisayar kimliği (Windows MachineGuid). Veritabanında SAKLANMAZ:
/// veritabanı başka bilgisayara kopyalanırsa iki bilgisayar aynı kimliği taşımasın.</param>
public sealed record StationIdentity(string InstallId, string? AppVersion);

/// <summary>
/// Depo sunucusuna bir gönderim turu (<c>server_outbox</c>). <see cref="SyncService"/> (Sheets) ile
/// aynı kurallar: 200'lük paketler, hata olunca tur durur ve kuyruk bekler, gönderim sırasında gelen
/// değişiklik kaybolmaz. Kayıt, baskı ve okutma bu servise bağlı değildir: sunucu kapalıyken QC aynen çalışır.
/// </summary>
public sealed class ServerSyncService(
    OutboxRepository outbox,
    SettingsRepository settings,
    StationCounters counters,
    IServerTransport transport,
    StationIdentity identity,
    TimeProvider clock) : ISyncRunner
{
    public const int BatchSize = 200;
    public const int MaxBatchesPerRun = 25;
    public static readonly TimeSpan KeepSentFor = TimeSpan.FromDays(30);

    /// <summary>Bu oturumda istasyon kaydı yapılıp sayaçları öğrenilen adres ve istasyon kodu.</summary>
    private (Uri Url, string Station)? _handshake;

    public OutboxRepository Outbox => outbox;

    public async Task<SyncRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (outbox.Table != OutboxTables.Server)
            throw new InvalidOperationException("ServerSyncService yalnızca server_outbox ile çalışır.");

        var baseUri = settings.GetServerBaseUri();
        if (baseUri is null) return new SyncRunResult(false, 0, null);
        var station = settings.GetStationCode();

        // Oturumdaki ilk gidiş (ya da adres/istasyon kodu değişti): önce BOŞ paket. Sunucu istasyon kodunu
        // bu bilgisayara kaydeder (başka bilgisayara kayıtlıysa reddeder, hiçbir satır yazılmaz) ve
        // sayaçları döndürür: yeni kurulum sunucunun bildiği bir ID'yi tekrar üretmez.
        if (_handshake != (baseUri, station))
        {
            try
            {
                var hello = await transport.SendCutsAsync(baseUri, Request(station, []), cancellationToken);
                counters.RaiseTo(hello.MaxSequences);
                _handshake = (baseUri, station);
            }
            catch (SyncException ex)
            {
                var waiting = outbox.GetPendingBatch(BatchSize);
                if (waiting.Count > 0) outbox.MarkFailed(waiting, ex.Message);
                return new SyncRunResult(true, 0, ex);
            }
        }

        var sent = 0;
        for (var i = 0; i < MaxBatchesPerRun; i++)
        {
            var batch = outbox.GetPendingBatch(BatchSize);
            if (batch.Count == 0) break;

            CutsBatchResponse result;
            try
            {
                result = await transport.SendCutsAsync(baseUri, Request(station, batch.Select(p => p.Row).ToList()), cancellationToken);
            }
            catch (SyncException ex)
            {
                outbox.MarkFailed(batch, ex.Message);
                // İstasyon reddi gibi kalıcı hatalarda el sıkışma tekrar yapılsın (yönetici kaydı serbest bırakmış olabilir).
                if (ex.Kind == SyncFailureKind.Rejected) _handshake = null;
                return new SyncRunResult(true, sent, ex);
            }

            counters.RaiseTo(result.MaxSequences);

            // Çakışan kesim sunucuda kayıt altına alındı (karantina) ama kuyrukta kalır: uyarı görünür kalsın.
            var conflicted = result.Conflicts.Select(c => c.CutId).ToHashSet(StringComparer.Ordinal);
            var accepted = batch.Where(p => !conflicted.Contains(p.Row.CutId)).ToList();
            outbox.MarkSent(accepted, clock.GetUtcNow());
            sent += accepted.Count;

            if (conflicted.Count > 0)
            {
                var failure = new SyncException(SyncFailureKind.Rejected, ConflictMessage(result.Conflicts));
                outbox.MarkFailed(batch.Where(p => conflicted.Contains(p.Row.CutId)).ToList(), failure.Message);
                return new SyncRunResult(true, sent, failure);
            }
        }

        outbox.PurgeSent(clock.GetUtcNow() - KeepSentFor);
        return new SyncRunResult(true, sent, null);
    }

    /// <summary>Ayarlar'daki "Bağlantıyı test et": sağlık kontrolü (veri yazılmaz, istasyon kaydı yapılmaz).</summary>
    public Task<HealthResponse> TestConnectionAsync(Uri baseUri, CancellationToken cancellationToken = default) =>
        transport.HealthAsync(baseUri, cancellationToken);

    private CutsBatchRequest Request(string station, IReadOnlyList<CutSyncRow> rows) =>
        new(identity.InstallId, station, identity.AppVersion, rows);

    public static string ConflictMessage(IReadOnlyList<ServerCutConflict> conflicts)
    {
        var first = conflicts[0];
        var more = conflicts.Count > 1 ? $" (ve {conflicts.Count - 1} kayıt daha)" : "";
        return $"{first.CutId}{more}: depo sunucusunda bu ID başka bir topa ait. İki top aynı etiketi taşıyor olabilir; " +
               "top karantinaya alındı, sevk edilemez. Yetkiliye bildirin.";
    }
}
