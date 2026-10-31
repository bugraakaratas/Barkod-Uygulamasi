using Dapper;
using KumasBarkod.Core.Domain;

namespace KumasBarkod.Core.Data;

/// <summary>Ekranların okuma sorguları.</summary>
public sealed class CutRepository(SqliteConnectionFactory db)
{
    /// <summary>Lot önerisi için en son kullanılan lotlar, yeniden eskiye, tekrarsız.</summary>
    public IReadOnlyList<string> GetRecentLots(int limit = 10)
    {
        using var c = db.Open();
        // Yalnızca son 2000 kesime bakılır: tablo büyüse de sorgu hızlı kalır (created_at indeksi).
        return c.Query<string>("""
            SELECT lot_code
            FROM (SELECT lot_code, created_at, sequence_no FROM cuts
                  ORDER BY created_at DESC, sequence_no DESC LIMIT 2000)
            GROUP BY lot_code
            ORDER BY max(created_at) DESC, max(sequence_no) DESC
            LIMIT @limit
            """, new { limit }).AsList();
    }

    public IReadOnlyList<Cut> GetRecentCuts(int limit = 10)
    {
        using var c = db.Open();
        return c.Query<Cut>($"""
            SELECT {Cut.SelectColumns} FROM cuts
            ORDER BY created_at DESC, sequence_no DESC
            LIMIT @limit
            """, new { limit }).AsList();
    }

    /// <summary>Sheets'e henüz gönderilmemiş kesim sayısı.</summary>
    public long CountPendingSync()
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT count(DISTINCT cut_id) FROM sync_outbox WHERE sent_at IS NULL");
    }
}
