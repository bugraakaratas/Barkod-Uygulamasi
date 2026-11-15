using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Server.Ingest;
using KumasBarkod.Server.Lots;

namespace KumasBarkod.Server.Stock;

/// <summary>Stok sorgusu. <paramref name="LotId"/> ve <paramref name="Unmatched"/> birlikte verilmez.</summary>
public sealed record StockQuery(
    string State = RollStateNames.InStock,
    long? LotId = null,
    bool Unmatched = false,
    string? Search = null,
    int Offset = 0,
    int Limit = 200);

/// <summary>
/// Depo stoğu (salt okunur). Sayılar her zaman gerçek top kayıtlarından hesaplanır (ayrı sayaç tutulmaz):
/// "depoda kaç top / kaç metre" = <c>state = 'in_stock'</c> topların sayısı ve uzunluk toplamı.
/// Özet ve liste aynı okuma işleminde: ekranda sayı ile liste birbirini tutar.
/// </summary>
public sealed class StockQueryService(SqliteConnectionFactory db)
{
    public const int MaxLimit = 500;

    private static readonly HashSet<string> States =
    [
        RollStateNames.InStock, RollStateNames.Reserved, RollStateNames.Shipped,
        RollStateNames.Voided, RollStateNames.Quarantined, RollStateNames.All,
    ];

    private const string RowColumns = """
        r.cut_id AS CutId, r.lot_id AS LotId, l.name AS LotName, r.lot_code_raw AS LotRaw, r.length_cm AS LengthCm,
        r.qc_created_at AS QcCreatedAt, r.first_seen_at AS FirstSeenAt, r.state AS State, r.qc_status AS QcStatus,
        r.void_reason AS VoidReason
        """;

    public StockResponse Query(StockQuery q)
    {
        if (!States.Contains(q.State))
            throw new ApiException(400, ServerErrorCodes.InvalidRequest, $"Geçersiz durum filtresi: {q.State}");
        var limit = Math.Clamp(q.Limit, 1, MaxLimit);
        var offset = Math.Max(0, q.Offset);

        var where = new List<string>();
        var p = new DynamicParameters();
        if (q.State != RollStateNames.All) { where.Add("r.state = @state"); p.Add("state", q.State); }
        if (q.LotId is { } lotId) { where.Add("r.lot_id = @lotId"); p.Add("lotId", lotId); }
        if (q.Unmatched) where.Add("r.lot_id IS NULL");

        var search = q.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            if (CutIds.TryParseScan(search, out var cutId))
            {
                where.Add("r.cut_id = @cutId");
                p.Add("cutId", cutId);
            }
            else
            {
                where.Add(@"(r.lot_code_raw LIKE @like ESCAPE '\' OR l.name LIKE @like ESCAPE '\')");
                p.Add("like", "%" + search.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%");
            }
        }

        var whereSql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
        p.Add("limit", limit);
        p.Add("offset", offset);

        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: true);

        var version = DataVersion.Get(c, tx);
        var inStock = c.QuerySingle<(long Count, long Cm)>(
            "SELECT count(*), coalesce(sum(length_cm), 0) FROM rolls WHERE state = 'in_stock'", transaction: tx);
        var filtered = c.QuerySingle<(long Count, long Cm)>(
            $"SELECT count(*), coalesce(sum(r.length_cm), 0) FROM rolls r LEFT JOIN lots l ON l.lot_id = r.lot_id {whereSql}", p, tx);
        var unmatched = c.ExecuteScalar<long>("SELECT count(DISTINCT lot_code_raw) FROM rolls WHERE lot_id IS NULL", transaction: tx);
        var quarantined = c.ExecuteScalar<long>("SELECT count(*) FROM rolls WHERE state = 'quarantined'", transaction: tx);
        var rows = c.Query<RowData>($"""
            SELECT {RowColumns}
            FROM rolls r LEFT JOIN lots l ON l.lot_id = r.lot_id
            {whereSql}
            ORDER BY r.qc_created_at DESC, r.cut_id DESC
            LIMIT @limit OFFSET @offset
            """, p, tx).Select(ToRow).ToList();

        tx.Commit();
        return new StockResponse(version, new StockSummary(inStock.Count, inStock.Cm), new StockSummary(filtered.Count, filtered.Cm),
            unmatched, quarantined, rows, offset, limit);
    }

    /// <summary>Bir topun bütün bilgisi ve kalite kontrolden itibaren bütün hareketleri. Barkod ya da ID kabul eder.</summary>
    public RollDetail? Find(string input)
    {
        if (!CutIds.TryParseScan(input, out var cutId)) return null;
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: true);
        var row = c.QuerySingleOrDefault<RowData>($"""
            SELECT {RowColumns}, r.station_code AS StationCode, r.print_count AS PrintCount
            FROM rolls r LEFT JOIN lots l ON l.lot_id = r.lot_id WHERE r.cut_id = @cutId
            """, new { cutId }, tx);
        if (row is null) return null;
        var movements = c.Query<MovementInfo>("""
            SELECT movement_id AS MovementId, kind AS Kind, from_state AS FromState, to_state AS ToState,
                   occurred_at AS OccurredAt, recorded_at AS RecordedAt, station_code AS StationCode, note AS Note
            FROM stock_movements WHERE cut_id = @cutId ORDER BY movement_id
            """, new { cutId }, tx).AsList();
        tx.Commit();
        return new RollDetail(ToRow(row), row.StationCode!, row.PrintCount, movements);
    }

    private static StockRow ToRow(RowData r) => new(r.CutId, CutIds.ToBarcode(r.CutId), r.LotId, r.LotName, r.LotRaw,
        r.LengthCm, r.QcCreatedAt, r.FirstSeenAt, r.State, r.QcStatus, r.VoidReason);

    private sealed class RowData
    {
        public string CutId { get; init; } = "";
        public long? LotId { get; init; }
        public string? LotName { get; init; }
        public string LotRaw { get; init; } = "";
        public long LengthCm { get; init; }
        public string QcCreatedAt { get; init; } = "";
        public string FirstSeenAt { get; init; } = "";
        public string State { get; init; } = "";
        public string QcStatus { get; init; } = "";
        public string? VoidReason { get; init; }
        public string? StationCode { get; init; }
        public long PrintCount { get; init; }
    }
}
