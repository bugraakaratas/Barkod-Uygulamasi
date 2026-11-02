using Dapper;
using KumasBarkod.Core.Domain;

namespace KumasBarkod.Core.Data;

/// <summary><c>station_counter</c>: istasyon başına son verilen sıra numarası.</summary>
public sealed class StationCounters(SqliteConnectionFactory db)
{
    public long GetLast(string stationCode)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long?>(
            "SELECT last_sequence FROM station_counter WHERE station_code = @stationCode",
            new { stationCode }) ?? 0;
    }

    /// <summary>
    /// Sayaçları en az verilen değerlere çıkarır, asla düşürmez. Başka bir veritabanının
    /// (yeniden kurulum, aynı kodlu ikinci bilgisayar) Sheets'e yazdığı numaralar tekrar verilmez.
    /// </summary>
    public void RaiseTo(IReadOnlyDictionary<string, long> maxSequences)
    {
        var valid = maxSequences
            .Where(kv => Stations.IsValidCode(kv.Key) && kv.Value > 0)
            .Select(kv => new { station = kv.Key, last = Math.Min(kv.Value, CutIds.MaxSequence) })
            .ToList();
        if (valid.Count == 0) return;

        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        c.Execute("""
            INSERT INTO station_counter (station_code, last_sequence) VALUES (@station, @last)
            ON CONFLICT (station_code) DO UPDATE SET last_sequence = max(last_sequence, excluded.last_sequence)
            """, valid, tx);
        tx.Commit();
    }
}
