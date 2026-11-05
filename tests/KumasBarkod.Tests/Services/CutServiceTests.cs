using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Services;
using KumasBarkod.Tests.Data;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Tests.Services;

public class CutServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 30, 15, 250, TimeSpan.FromHours(3));

    private readonly TempDatabase _db = new();
    private readonly SettingsRepository _settings;
    private readonly CutService _service;

    public CutServiceTests()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        _settings = new SettingsRepository(_db.Factory);
        _service = new CutService(_db.Factory, _settings, new FixedClock(Now));
    }

    public void Dispose() => _db.Dispose();

    // ---------- Sıradaki parça ----------

    private void MarkPrinted(string cutId)
    {
        using var c = _db.Factory.Open();
        c.Execute("UPDATE cuts SET print_count = print_count + 1 WHERE cut_id = @cutId", new { cutId });
    }

    private long OutboxRows(string cutId)
    {
        using var c = _db.Factory.Open();
        return c.ExecuteScalar<long>("SELECT count(*) FROM sync_outbox WHERE cut_id = @cutId", new { cutId });
    }

    [Fact]
    public void Next_piece_copies_lot_and_length_with_next_id()
    {
        var first = _service.CreateCut("LOT-A", 2550, "Ayşe");
        MarkPrinted(first.CutId);

        var next = _service.CreateNextPiece(first.CutId);

        Assert.Equal("K1-000002", next.CutId);
        Assert.Equal("LOT-A", next.LotCode);
        Assert.Equal(2550, next.LengthCm);
        Assert.Equal("Ayşe", next.Operator);
        Assert.Equal(0, next.PrintCount);
        Assert.Equal(1, OutboxRows(next.CutId));
    }

    [Fact]
    public void Next_piece_can_chain_from_each_new_cut()
    {
        var cut = _service.CreateCut("LOT-A", 2550);
        for (var i = 0; i < 3; i++)
        {
            MarkPrinted(cut.CutId);
            cut = _service.CreateNextPiece(cut.CutId);
        }

        Assert.Equal("K1-000004", cut.CutId);
    }

    [Fact]
    public void Next_piece_refuses_voided_source_without_using_a_sequence()
    {
        var first = _service.CreateCut("LOT-A", 2550);
        MarkPrinted(first.CutId);
        _service.VoidCut(first.CutId, VoidReasons.WrongLength);

        var ex = Assert.Throws<CutValidationException>(() => _service.CreateNextPiece(first.CutId));

        Assert.Contains("iptal", ex.Message);
        Assert.Equal("K1-000002", _service.CreateCut("LOT-A", 100).CutId);
    }

    [Fact]
    public void Next_piece_refuses_source_whose_label_was_never_printed()
    {
        var first = _service.CreateCut("LOT-A", 2550);

        var ex = Assert.Throws<CutValidationException>(() => _service.CreateNextPiece(first.CutId));

        Assert.Contains("F6", ex.Message);
        Assert.Equal("K1-000002", _service.CreateCut("LOT-A", 100).CutId);
    }

    [Fact]
    public void Next_piece_refuses_unknown_source()
    {
        Assert.Throws<CutValidationException>(() => _service.CreateNextPiece("K1-999999"));
    }

    // ---------- Sıralı ID ----------

    [Fact]
    public void First_cut_gets_sequence_one_with_default_station()
    {
        var cut = _service.CreateCut("LOT-A", 2550);

        Assert.Equal("K1-000001", cut.CutId);
        Assert.Equal("K1000001", cut.Barcode);
        Assert.Equal(1, cut.SequenceNo);
    }

    [Fact]
    public void Ids_are_sequential_without_gaps()
    {
        var ids = Enumerable.Range(0, 25).Select(_ => _service.CreateCut("LOT-A", 100).CutId).ToList();

        var expected = Enumerable.Range(1, 25).Select(i => $"K1-{i:D6}").ToList();
        Assert.Equal(expected, ids);
    }

    [Fact]
    public void Each_station_has_its_own_counter()
    {
        _service.CreateCut("LOT-A", 100);
        _service.CreateCut("LOT-A", 100);
        _settings.SetStationCode("K2");

        Assert.Equal("K2-000001", _service.CreateCut("LOT-A", 100).CutId);

        _settings.SetStationCode("K1");
        Assert.Equal("K1-000003", _service.CreateCut("LOT-A", 100).CutId);
    }

    [Fact]
    public void Counter_full_gives_clear_error_and_writes_nothing()
    {
        using (var c = _db.Factory.Open())
            c.Execute("INSERT INTO station_counter (station_code, last_sequence) VALUES ('K1', 999999)");

        var ex = Assert.Throws<CutValidationException>(() => _service.CreateCut("LOT-A", 100));
        Assert.Contains("doldu", ex.Message);
        Assert.Equal(0, CountCuts());
    }

    [Fact]
    public void Parallel_creation_never_duplicates_ids()
    {
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 40, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            _ => ids.Add(_service.CreateCut("LOT-A", 100).CutId));

        Assert.Equal(40, ids.Distinct().Count());
        Assert.Equal(40, CountCuts());
        Assert.Equal(40, LastSequence("K1"));
    }

    // ---------- Kayıt içeriği ----------

    [Fact]
    public void Cut_is_stored_with_all_fields_and_utc_timestamp()
    {
        var created = _service.CreateCut("  LOT-A  ", 2550, "  Ahmet ");
        var stored = _service.FindById(created.CutId)!;

        Assert.Equal("LOT-A", stored.LotCode);
        Assert.Equal(2550, stored.LengthCm);
        Assert.Equal("Ahmet", stored.Operator);
        Assert.Equal(CutStatus.Active, stored.Status);
        Assert.Equal(0, stored.PrintCount);
        Assert.Null(stored.VoidedAt);
        Assert.Equal("2026-11-01T06:30:15.250Z", stored.CreatedAt); // +03:00 → UTC
    }

    [Fact]
    public void Blank_operator_is_stored_as_null()
    {
        var cut = _service.CreateCut("LOT-A", 100, "   ");
        Assert.Null(_service.FindById(cut.CutId)!.Operator);
    }

    [Fact]
    public void Outbox_row_is_written_with_the_cut()
    {
        var cut = _service.CreateCut("LOT-A", 2550);

        using var c = _db.Factory.Open();
        var row = c.QuerySingle<(string cut_id, string operation, string? sent_at, long attempts)>(
            "SELECT cut_id, operation, sent_at, attempts FROM sync_outbox");
        Assert.Equal((cut.CutId, "upsert", null, 0L), row);
    }

    [Fact]
    public void Lot_case_is_preserved()
    {
        var cut = _service.CreateCut("lot-ışık", 100);
        Assert.Equal("lot-ışık", _service.FindById(cut.CutId)!.LotCode);
    }

    // ---------- Doğrulama ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("LOT\tA")]
    public void Invalid_lot_is_rejected(string lot)
    {
        Assert.Throws<CutValidationException>(() => _service.CreateCut(lot, 100));
        Assert.Equal(0, CountCuts());
    }

    [Fact]
    public void Too_long_lot_is_rejected()
    {
        Assert.Throws<CutValidationException>(() => _service.CreateCut(new string('A', 51), 100));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(30001)] // varsayılan üst sınır 300 m
    public void Invalid_length_is_rejected(long cm)
    {
        Assert.Throws<CutValidationException>(() => _service.CreateCut("LOT-A", cm));
        Assert.Equal(0, CountCuts());
    }

    [Fact]
    public void Max_length_comes_from_settings()
    {
        _settings.SetMaxLengthCm(500_00);
        Assert.Equal(400_00, _service.CreateCut("LOT-A", 400_00).LengthCm);
    }

    [Fact]
    public void Rejected_input_does_not_consume_a_sequence_number()
    {
        Assert.Throws<CutValidationException>(() => _service.CreateCut("", 100));
        Assert.Equal("K1-000001", _service.CreateCut("LOT-A", 100).CutId);
    }

    // ---------- İptal ----------

    [Fact]
    public void Void_sets_status_reason_time_and_queues_sync()
    {
        var cut = _service.CreateCut("LOT-A", 2550);

        var voided = _service.VoidCut(cut.CutId, "  Yanlış metre  ");

        Assert.Equal(CutStatus.Voided, voided.Status);
        Assert.Equal("Yanlış metre", voided.VoidReason);
        Assert.Equal("2026-11-01T06:30:15.250Z", voided.VoidedAt);
        Assert.True(voided.IsVoided);

        using var c = _db.Factory.Open();
        Assert.Equal(2L, c.ExecuteScalar<long>("SELECT count(*) FROM sync_outbox WHERE cut_id = @id", new { id = cut.CutId }));
    }

    [Fact]
    public void Void_keeps_original_data()
    {
        var cut = _service.CreateCut("LOT-A", 2550);
        var voided = _service.VoidCut(cut.CutId, "Hatalı lot");

        Assert.Equal("LOT-A", voided.LotCode);
        Assert.Equal(2550, voided.LengthCm);
        Assert.Equal(cut.CreatedAt, voided.CreatedAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("satır\nsonu")]
    public void Void_requires_a_valid_reason(string? reason)
    {
        var cut = _service.CreateCut("LOT-A", 2550);

        Assert.Throws<CutValidationException>(() => _service.VoidCut(cut.CutId, reason));
        Assert.Equal(CutStatus.Active, _service.FindById(cut.CutId)!.Status);
    }

    [Fact]
    public void Void_reason_length_is_limited()
    {
        var cut = _service.CreateCut("LOT-A", 2550);
        Assert.Throws<CutValidationException>(() => _service.VoidCut(cut.CutId, new string('x', 201)));
    }

    [Fact]
    public void Void_twice_is_rejected()
    {
        var cut = _service.CreateCut("LOT-A", 2550);
        _service.VoidCut(cut.CutId, "Birinci");

        var ex = Assert.Throws<CutValidationException>(() => _service.VoidCut(cut.CutId, "İkinci"));
        Assert.Contains("zaten", ex.Message);
        Assert.Equal("Birinci", _service.FindById(cut.CutId)!.VoidReason);
    }

    [Fact]
    public void Void_unknown_cut_is_rejected()
    {
        Assert.Throws<CutValidationException>(() => _service.VoidCut("K1-999999", "sebep"));
    }

    [Fact]
    public void Fixed_void_reasons_are_four_distinct_valid_texts()
    {
        Assert.Equal(
            ["Yanlış metre girildi", "Yanlış lot girildi", "Çift kayıt", "Kesim yapılmadı / vazgeçildi"],
            VoidReasons.All);
        Assert.Equal(VoidReasons.All.Count, VoidReasons.All.Distinct().Count());
    }

    [Fact]
    public void Each_fixed_void_reason_is_stored_verbatim()
    {
        foreach (var reason in VoidReasons.All)
        {
            var cut = _service.CreateCut("LOT-A", 100);
            Assert.Equal(reason, _service.VoidCut(cut.CutId, reason).VoidReason);
        }
    }

    [Fact]
    public void Voiding_does_not_affect_next_sequence()
    {
        var cut = _service.CreateCut("LOT-A", 100);
        _service.VoidCut(cut.CutId, "Hata");
        Assert.Equal("K1-000002", _service.CreateCut("LOT-A", 100).CutId);
    }

    // ---------- Bütünlük ----------

    [Fact]
    public void Failure_midway_rolls_back_counter_and_cut()
    {
        _service.CreateCut("LOT-A", 100);

        // Kuyruk yazımını bozarak işlemin ortasında hata oluştur
        using (var c = _db.Factory.Open())
            c.Execute("ALTER TABLE sync_outbox RENAME TO sync_outbox_broken");

        Assert.Throws<SqliteException>(() => _service.CreateCut("LOT-A", 100));

        Assert.Equal(1, CountCuts());
        Assert.Equal(1, LastSequence("K1"));

        using (var c = _db.Factory.Open())
            c.Execute("ALTER TABLE sync_outbox_broken RENAME TO sync_outbox");

        Assert.Equal("K1-000002", _service.CreateCut("LOT-A", 100).CutId);
    }

    [Fact]
    public void Crash_before_commit_leaves_no_partial_record_on_disk()
    {
        _service.CreateCut("LOT-A", 100);

        // İşlem açıkken diskteki dosyaların kopyasını al: bu, o anda elektrik
        // kesilse diskte kalacak durumdur.
        using var c = _db.Factory.Open();
        using var tx = c.BeginTransaction(deferred: false);
        c.Execute("UPDATE station_counter SET last_sequence = 2 WHERE station_code = 'K1'", transaction: tx);
        c.Execute("""
            INSERT INTO cuts (cut_id, station_code, sequence_no, lot_code, length_cm, status, created_at)
            VALUES ('K1-000002', 'K1', 2, 'LOT-A', 100, 'active', '2026-11-01T06:30:15.250Z')
            """, transaction: tx);

        var snapshot = SnapshotDatabaseFiles();
        tx.Rollback();

        using var recovered = OpenSnapshot(snapshot);
        Assert.Equal(1L, recovered.ExecuteScalar<long>("SELECT count(*) FROM cuts"));
        Assert.Equal(1L, recovered.ExecuteScalar<long>("SELECT count(*) FROM sync_outbox"));
        Assert.Equal(1L, recovered.ExecuteScalar<long>("SELECT last_sequence FROM station_counter"));
        Assert.Equal("ok", recovered.ExecuteScalar<string>("PRAGMA integrity_check"));
    }

    [Fact]
    public void Committed_cut_survives_crash_even_before_checkpoint()
    {
        // Bağlantı açık kalsın: WAL ana dosyaya aktarılmadan "elektrik kesilsin"
        using var keepOpen = _db.Factory.Open();
        _service.CreateCut("LOT-A", 2550);

        var snapshot = SnapshotDatabaseFiles();

        // Testin anlamlı olduğunu doğrula: WAL olmadan ana dosyada kayıt henüz yok
        var mainOnly = Path.Combine(Path.GetDirectoryName(snapshot)!, "main-only.db");
        File.Copy(snapshot, mainOnly);
        using (var withoutWal = OpenSnapshot(mainOnly))
        {
            var hasTable = withoutWal.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE name = 'cuts'") > 0;
            var rows = hasTable ? withoutWal.ExecuteScalar<long>("SELECT count(*) FROM cuts") : 0;
            Assert.Equal(0L, rows);
        }

        using var recovered = OpenSnapshot(snapshot);
        Assert.Equal("K1-000001", recovered.ExecuteScalar<string>("SELECT cut_id FROM cuts"));
        Assert.Equal(1L, recovered.ExecuteScalar<long>("SELECT count(*) FROM sync_outbox"));
        Assert.Equal("ok", recovered.ExecuteScalar<string>("PRAGMA integrity_check"));
    }

    // ---------- Yardımcılar ----------

    private long CountCuts()
    {
        using var c = _db.Factory.Open();
        return c.ExecuteScalar<long>("SELECT count(*) FROM cuts");
    }

    private long LastSequence(string station)
    {
        using var c = _db.Factory.Open();
        return c.ExecuteScalar<long>("SELECT last_sequence FROM station_counter WHERE station_code = @station", new { station });
    }

    /// <summary>Veritabanı ve WAL dosyasını (açıkken) başka klasöre kopyalar.</summary>
    private string SnapshotDatabaseFiles()
    {
        var source = _db.Factory.DatabasePath;
        var dir = Path.Combine(Path.GetDirectoryName(source)!, "snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(source));

        foreach (var suffix in new[] { "", "-wal" })
        {
            if (!File.Exists(source + suffix)) continue;
            using var input = new FileStream(source + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = File.Create(target + suffix);
            input.CopyTo(output);
        }
        return target;
    }

    private static SqliteConnection OpenSnapshot(string path)
    {
        var c = new SqliteConnection($"Data Source={path};Pooling=False");
        c.Open();
        return c;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
