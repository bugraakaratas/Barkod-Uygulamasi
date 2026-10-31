using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;

namespace KumasBarkod.Core.Sync;

/// <summary>Bekleyen bir kesim: gönderilecek güncel hâli ve bu turda kapsanan en büyük kuyruk numarası.</summary>
public sealed record PendingCut(CutSyncRow Row, long MaxOutboxId);

/// <summary>
/// <c>sync_outbox</c> kuyruğu. Bir kesim için birden çok bekleyen satır olabilir (oluşturma, baskı,
/// iptal); gönderimde kesimin O ANKİ hâli tek satır olarak gider. Gönderim sırasında yeni bir
/// değişiklik gelirse (daha büyük kuyruk numarası) o satır bekler ve sonraki turda gider.
/// </summary>
public sealed class OutboxRepository(SqliteConnectionFactory db)
{
    public IReadOnlyList<PendingCut> GetPendingBatch(int limit)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: true);   // kuyruk ve kesim aynı anlık görüntüden

        var pending = c.Query<(string CutId, long MaxId)>("""
            SELECT cut_id, max(id) FROM sync_outbox
            WHERE sent_at IS NULL
            GROUP BY cut_id
            ORDER BY min(id)
            LIMIT @limit
            """, new { limit }, tx).AsList();

        var result = new List<PendingCut>(pending.Count);
        foreach (var (cutId, maxId) in pending)
        {
            var cut = c.QuerySingle<Cut>($"SELECT {Cut.SelectColumns} FROM cuts WHERE cut_id = @cutId", new { cutId }, tx);
            result.Add(new PendingCut(CutSyncRow.From(cut), maxId));
        }

        tx.Commit();
        return result;
    }

    /// <summary>Gönderilen kesimlerin, gönderilen numaraya kadarki kuyruk satırlarını kapatır.</summary>
    public void MarkSent(IReadOnlyList<PendingCut> batch, DateTimeOffset sentAtUtc)
    {
        var sentAt = Timestamps.Format(sentAtUtc);
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        foreach (var p in batch)
        {
            c.Execute("""
                UPDATE sync_outbox SET sent_at = @sentAt
                WHERE cut_id = @cutId AND sent_at IS NULL AND id <= @maxId
                """, new { sentAt, cutId = p.Row.CutId, maxId = p.MaxOutboxId }, tx);
        }
        tx.Commit();
    }

    public void MarkFailed(IReadOnlyList<PendingCut> batch, string error)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        foreach (var p in batch)
        {
            c.Execute("""
                UPDATE sync_outbox SET attempts = attempts + 1, last_error = @error
                WHERE cut_id = @cutId AND sent_at IS NULL AND id <= @maxId
                """, new { error, cutId = p.Row.CutId, maxId = p.MaxOutboxId }, tx);
        }
        tx.Commit();
    }

    /// <summary>Gönderilmiş eski kuyruk satırlarını siler (tablo sonsuza dek büyümesin).</summary>
    public int PurgeSent(DateTimeOffset olderThanUtc)
    {
        using var c = db.Open();
        return c.Execute("DELETE FROM sync_outbox WHERE sent_at IS NOT NULL AND sent_at < @t",
            new { t = Timestamps.Format(olderThanUtc) });
    }

    public long CountPendingCuts()
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT count(DISTINCT cut_id) FROM sync_outbox WHERE sent_at IS NULL");
    }
}
