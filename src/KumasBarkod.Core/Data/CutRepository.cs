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

    public const int MaxPageSize = 1000;

    /// <summary>Geçmiş ekranı: filtreli, sayfalı arama ve özet toplamlar.</summary>
    public CutSearchResult Search(CutQuery query, TimeZoneInfo timeZone)
    {
        if (query.From is { } f && query.To is { } t && f > t)
            throw new CutValidationException("Başlangıç tarihi bitiş tarihinden sonra olamaz.");

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var page = Math.Max(0, query.Page);

        var where = new List<string>();
        var p = new DynamicParameters();

        if (query.From is { } from)
        {
            where.Add("created_at >= @fromUtc");
            p.Add("fromUtc", LocalDayStartUtc(from, timeZone));
        }
        if (query.To is { } to)
        {
            where.Add("created_at < @toUtc");      // bitiş günü dahil: ertesi günün başından küçük
            p.Add("toUtc", LocalDayStartUtc(to.AddDays(1), timeZone));
        }

        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            if (CutIds.TryParseScan(search, out var cutId))
            {
                where.Add("cut_id = @cutId");
                p.Add("cutId", cutId);
            }
            else
            {
                where.Add(@"lot_code LIKE @lotLike ESCAPE '\'");
                p.Add("lotLike", "%" + EscapeLike(search) + "%");
            }
        }

        switch (query.Status)
        {
            case CutStatusFilter.Active: where.Add("status = 'active'"); break;
            case CutStatusFilter.Voided: where.Add("status = 'voided'"); break;
        }

        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);

        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: true);   // özet ve sayfa aynı anlık görüntüden okunur

        var summary = c.QuerySingle<(long Total, long Active, long Voided, long ActiveCm)>($"""
            SELECT count(*),
                   coalesce(sum(status = 'active'), 0),
                   coalesce(sum(status = 'voided'), 0),
                   coalesce(sum(CASE WHEN status = 'active' THEN length_cm ELSE 0 END), 0)
            FROM cuts {whereSql}
            """, p, tx);

        p.Add("limit", pageSize);
        p.Add("offset", (long)page * pageSize);
        var items = c.Query<Cut>($"""
            SELECT {Cut.SelectColumns} FROM cuts {whereSql}
            ORDER BY created_at DESC, sequence_no DESC
            LIMIT @limit OFFSET @offset
            """, p, tx).AsList();

        tx.Commit();
        return new CutSearchResult(items, summary.Total, summary.Active, summary.Voided, summary.ActiveCm, page, pageSize);
    }

    private static string LocalDayStartUtc(DateOnly day, TimeZoneInfo timeZone) =>
        Timestamps.Format(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), timeZone));

    /// <summary>LIKE joker karakterleri (% _) ve kaçış karakteri (\) düz metin olarak aransın.</summary>
    private static string EscapeLike(string text) => text
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    /// <summary>Sheets'e henüz gönderilmemiş kesim sayısı.</summary>
    public long CountPendingSync()
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT count(DISTINCT cut_id) FROM sync_outbox WHERE sent_at IS NULL");
    }
}
