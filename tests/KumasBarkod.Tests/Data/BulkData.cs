using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Tests.Data;

/// <summary>Hız testleri için hızlı toplu kayıt (tek işlemde, hazır komutla).</summary>
public static class BulkData
{
    /// <summary>
    /// <paramref name="count"/> kesim: <paramref name="start"/>'tan geriye doğru eşit aralıklı zaman,
    /// 500 farklı lot, her 33 kayıttan biri iptal.
    /// </summary>
    public static void Insert(SqliteConnectionFactory factory, int count, DateTime startUtc, TimeSpan step)
    {
        using var c = factory.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO cuts (cut_id, station_code, sequence_no, lot_code, length_cm, status, void_reason, voided_at, created_at, print_count)
            VALUES ($id, 'K1', $seq, $lot, $len, $status, $reason, $voidedAt, $created, 1)
            """;
        var id = cmd.Parameters.Add("$id", SqliteType.Text);
        var seq = cmd.Parameters.Add("$seq", SqliteType.Integer);
        var lot = cmd.Parameters.Add("$lot", SqliteType.Text);
        var len = cmd.Parameters.Add("$len", SqliteType.Integer);
        var status = cmd.Parameters.Add("$status", SqliteType.Text);
        var reason = cmd.Parameters.Add("$reason", SqliteType.Text);
        var voidedAt = cmd.Parameters.Add("$voidedAt", SqliteType.Text);
        var created = cmd.Parameters.Add("$created", SqliteType.Text);
        cmd.Prepare();

        for (var i = 1; i <= count; i++)
        {
            var at = Timestamps.Format(startUtc - step * (count - i));
            var voided = i % 33 == 0;
            id.Value = CutIds.Format("K1", i);
            seq.Value = i;
            lot.Value = $"LOT-{i % 500:D4}";
            len.Value = 100 + i % 5000;
            status.Value = voided ? CutStatus.Voided : CutStatus.Active;
            reason.Value = voided ? "test" : DBNull.Value;
            voidedAt.Value = voided ? at : DBNull.Value;
            created.Value = at;
            cmd.ExecuteNonQuery();
        }

        using var counter = c.CreateCommand();
        counter.Transaction = tx;
        counter.CommandText = $"INSERT INTO station_counter (station_code, last_sequence) VALUES ('K1', {count})";
        counter.ExecuteNonQuery();

        tx.Commit();
    }
}
