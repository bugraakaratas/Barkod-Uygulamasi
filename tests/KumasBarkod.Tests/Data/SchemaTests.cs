using KumasBarkod.Core.Data;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Tests.Data;

public class SchemaTests : IDisposable
{
    private readonly TempDatabase _db = new();

    public void Dispose() => _db.Dispose();

    // ---------- Göç ----------

    [Fact]
    public void Migrate_creates_database_file_and_all_tables()
    {
        Assert.False(File.Exists(_db.Factory.DatabasePath));

        var applied = DatabaseMigrator.Migrate(_db.Factory);

        Assert.True(File.Exists(_db.Factory.DatabasePath));
        Assert.Equal(Migrations.All.Count, applied);

        using var c = _db.Factory.Open();
        var tables = Query<string>(c, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
        Assert.Equal(["cuts", "settings", "station_counter", "sync_outbox"], tables);
        Assert.Equal(Migrations.LatestVersion, DatabaseMigrator.GetSchemaVersion(c));
    }

    [Fact]
    public void Migrate_is_idempotent()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        var secondRun = DatabaseMigrator.Migrate(_db.Factory);

        Assert.Equal(0, secondRun);
    }

    [Fact]
    public void Migrate_refuses_database_from_newer_app_version()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        using (var c = _db.Factory.Open())
            Exec(c, $"PRAGMA user_version = {Migrations.LatestVersion + 1}");

        Assert.Throws<InvalidOperationException>(() => DatabaseMigrator.Migrate(_db.Factory));
    }

    [Fact]
    public void Expected_indexes_exist()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        using var c = _db.Factory.Open();

        var indexes = Query<string>(c, "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'ix_%' ORDER BY name");
        Assert.Equal(["ix_cuts_created_at", "ix_cuts_lot_code", "ix_sync_outbox_pending"], indexes);
    }

    // ---------- Bağlantı ayarları ----------

    [Fact]
    public void Connection_uses_wal_full_sync_and_foreign_keys()
    {
        using var c = _db.Factory.Open();

        Assert.Equal("wal", Scalar<string>(c, "PRAGMA journal_mode"));
        Assert.Equal(2L, Scalar<long>(c, "PRAGMA synchronous"));   // 2 = FULL
        Assert.Equal(1L, Scalar<long>(c, "PRAGMA foreign_keys"));
    }

    // ---------- Benzersizlik ----------

    [Fact]
    public void Valid_cut_is_accepted()
    {
        using var c = MigratedConnection();
        InsertCut(c);
        Assert.Equal(1L, Scalar<long>(c, "SELECT count(*) FROM cuts"));
    }

    [Fact]
    public void Duplicate_cut_id_is_rejected()
    {
        using var c = MigratedConnection();
        InsertCut(c);
        AssertConstraintViolation(() => InsertCut(c));
    }

    [Fact]
    public void Same_sequence_on_same_station_is_rejected_even_with_different_id_formatting()
    {
        using var c = MigratedConnection();
        InsertCut(c, id: "K1-000005", station: "K1", seq: 5);
        // Aynı istasyon + sıra; ID kuralı da zaten farklı yazımı engeller
        AssertConstraintViolation(() => InsertCut(c, id: "K1-5", station: "K1", seq: 5));
    }

    [Fact]
    public void Same_sequence_on_different_stations_is_allowed()
    {
        using var c = MigratedConnection();
        InsertCut(c, id: "K1-000001", station: "K1", seq: 1);
        InsertCut(c, id: "K2-000001", station: "K2", seq: 1);
        Assert.Equal(2L, Scalar<long>(c, "SELECT count(*) FROM cuts"));
    }

    // ---------- Veri kuralları ----------

    [Theory]
    [InlineData("K1-000002")]   // sıra numarasıyla uyuşmuyor
    [InlineData("K1000001")]    // tiresiz (barkod biçimi) saklanmaz
    [InlineData("k1-000001")]   // küçük harf
    public void Cut_id_must_match_station_and_sequence(string badId)
    {
        using var c = MigratedConnection();
        AssertConstraintViolation(() => InsertCut(c, id: badId, station: "K1", seq: 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" LOT1")]
    [InlineData("LOT1 ")]
    public void Lot_code_must_be_non_empty_and_trimmed(string lot)
    {
        using var c = MigratedConnection();
        AssertConstraintViolation(() => InsertCut(c, lot: lot));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-150)]
    public void Length_must_be_positive(long lengthCm)
    {
        using var c = MigratedConnection();
        AssertConstraintViolation(() => InsertCut(c, lengthCm: lengthCm));
    }

    [Fact]
    public void Length_must_be_integer_strict_table()
    {
        using var c = MigratedConnection();
        // STRICT tablo: 12.5 gibi kesirli değer INTEGER sütuna girmez
        AssertConstraintViolation(() => InsertCut(c, lengthCm: 12.5));
    }

    [Fact]
    public void Unknown_status_is_rejected()
    {
        using var c = MigratedConnection();
        AssertConstraintViolation(() => InsertCut(c, status: "deleted"));
    }

    [Fact]
    public void Voided_cut_requires_voided_at()
    {
        using var c = MigratedConnection();
        AssertConstraintViolation(() => InsertCut(c, status: "voided", voidedAt: null));
    }

    [Fact]
    public void Active_cut_cannot_carry_void_info()
    {
        using var c = MigratedConnection();
        AssertConstraintViolation(() => InsertCut(c, status: "active", voidedAt: "2026-11-01T10:00:00Z"));
    }

    [Fact]
    public void Voided_cut_with_timestamp_is_accepted()
    {
        using var c = MigratedConnection();
        InsertCut(c, status: "voided", voidedAt: "2026-11-01T10:00:00Z");
        Assert.Equal("voided", Scalar<string>(c, "SELECT status FROM cuts"));
    }

    [Fact]
    public void Outbox_rejects_unknown_cut_id()
    {
        using var c = MigratedConnection();
        AssertConstraintViolation(() => Exec(c,
            "INSERT INTO sync_outbox (cut_id, operation, created_at) VALUES ('K1-999999', 'upsert', '2026-11-01T10:00:00Z')"));
    }

    [Fact]
    public void Outbox_accepts_known_cut_and_defaults_attempts_to_zero()
    {
        using var c = MigratedConnection();
        InsertCut(c);
        Exec(c, "INSERT INTO sync_outbox (cut_id, operation, created_at) VALUES ('K1-000001', 'upsert', '2026-11-01T10:00:00Z')");

        Assert.Equal(0L, Scalar<long>(c, "SELECT attempts FROM sync_outbox"));
        Assert.Equal(1L, Scalar<long>(c, "SELECT count(*) FROM sync_outbox WHERE sent_at IS NULL"));
    }

    // ---------- Yardımcılar ----------

    private SqliteConnection MigratedConnection()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        return _db.Factory.Open();
    }

    private static void InsertCut(
        SqliteConnection c,
        string id = "K1-000001", string station = "K1", long seq = 1,
        string lot = "LOT-A", object? lengthCm = null,
        string status = "active", string? voidedAt = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cuts (cut_id, station_code, sequence_no, lot_code, length_cm, status, voided_at, created_at)
            VALUES ($id, $station, $seq, $lot, $len, $status, $voidedAt, '2026-11-01T09:00:00Z')
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$station", station);
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.Parameters.AddWithValue("$lot", lot);
        cmd.Parameters.AddWithValue("$len", lengthCm ?? 2550L);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$voidedAt", (object?)voidedAt ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void AssertConstraintViolation(Action action)
    {
        var ex = Assert.Throws<SqliteException>(action);
        Assert.Equal(19, ex.SqliteErrorCode); // SQLITE_CONSTRAINT
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (T)cmd.ExecuteScalar()!;
    }

    private static List<T> Query<T>(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(reader.GetFieldValue<T>(0));
        return list;
    }
}
