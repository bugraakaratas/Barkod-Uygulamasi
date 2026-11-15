using Dapper;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Core.Data;

/// <summary>Gönderim kuyruğu tabloları. Tablo adı SQL'e yalnızca bu sabitlerden girer.</summary>
public static class OutboxTables
{
    /// <summary>Google Sheets kopyası.</summary>
    public const string Sheets = "sync_outbox";

    /// <summary>Depo sunucusu (stok ve sevkiyat).</summary>
    public const string Server = "server_outbox";

    public static string Validate(string table) => table is Sheets or Server
        ? table
        : throw new ArgumentException($"Bilinmeyen kuyruk tablosu: {table}", nameof(table));
}

/// <summary>
/// Kesimdeki her değişiklik (oluşturma, baskı, iptal) bütün hedeflerin kuyruğuna AYNI işlem
/// içinde yazılır: kayıt kalırsa kuyruk satırları da kalır, hiçbir hedef değişikliği kaçırmaz.
/// </summary>
public static class OutboxWriter
{
    public static void EnqueueAll(SqliteConnection c, SqliteTransaction tx, string cutId, string now)
    {
        Enqueue(c, tx, OutboxTables.Sheets, cutId, now);
        Enqueue(c, tx, OutboxTables.Server, cutId, now);
    }

    public static void Enqueue(SqliteConnection c, SqliteTransaction tx, string table, string cutId, string now) =>
        c.Execute($"INSERT INTO {OutboxTables.Validate(table)} (cut_id, operation, created_at) VALUES (@cutId, 'upsert', @now)",
            new { cutId, now }, tx);
}
