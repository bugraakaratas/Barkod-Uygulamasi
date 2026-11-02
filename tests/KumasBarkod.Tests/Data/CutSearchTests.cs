using System.Diagnostics;
using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Services;
using Xunit.Abstractions;

namespace KumasBarkod.Tests.Data;

/// <summary>
/// Hız ölçümü içeren sınıflar diğer testlerle paralel ÇALIŞMAZ: paralel testler CPU'yu paylaşınca
/// 1 sn sınırı ara sıra aşılıyordu (kurulum paketi derlemesini durdurdu). Ölçüm tek başına yapılır.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Hız ölçümleri (paralel değil)";
}

[Collection(TimingCollection.Name)]
public class CutSearchTests : IDisposable
{
    private static readonly TimeZoneInfo Istanbul =
        TimeZoneInfo.CreateCustomTimeZone("TR", TimeSpan.FromHours(3), "TR", "TR");

    private readonly TempDatabase _db = new();
    private readonly CutRepository _repo;
    private readonly SettableClock _clock = new();
    private readonly CutService _service;
    private readonly ITestOutputHelper _output;

    public CutSearchTests(ITestOutputHelper output)
    {
        _output = output;
        DatabaseMigrator.Migrate(_db.Factory);
        _repo = new CutRepository(_db.Factory);
        _service = new CutService(_db.Factory, new SettingsRepository(_db.Factory), _clock);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Yerel (İstanbul) saatle kesim oluştur.</summary>
    private Cut CutAt(int day, int hour, string lot = "LOT-A", long cm = 1000, int month = 11)
    {
        _clock.Now = new DateTimeOffset(2026, month, day, hour, 0, 0, TimeSpan.FromHours(3));
        return _service.CreateCut(lot, cm);
    }

    private CutSearchResult Search(CutQuery q) => _repo.Search(q, Istanbul);

    // ---------- Tarih ----------

    [Fact]
    public void Date_range_is_inclusive_in_local_time()
    {
        CutAt(1, 0);      // 1 Kasım 00:00 yerel = 31 Ekim 21:00 UTC
        CutAt(1, 23);
        CutAt(2, 0);
        CutAt(3, 12);

        var r = Search(new CutQuery(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2)));

        Assert.Equal(["K1-000003", "K1-000002", "K1-000001"], r.Items.Select(c => c.CutId));
    }

    [Fact]
    public void Single_day_filter()
    {
        CutAt(1, 8);
        CutAt(2, 8);
        var r = Search(new CutQuery(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 2)));
        Assert.Equal("K1-000002", Assert.Single(r.Items).CutId);
    }

    [Fact]
    public void Open_ended_ranges()
    {
        CutAt(1, 8);
        CutAt(5, 8);
        CutAt(9, 8);

        Assert.Equal(2, Search(new CutQuery(From: new DateOnly(2026, 11, 5))).TotalCount);
        Assert.Equal(2, Search(new CutQuery(To: new DateOnly(2026, 11, 5))).TotalCount);
        Assert.Equal(3, Search(new CutQuery()).TotalCount);
    }

    [Fact]
    public void From_after_to_is_rejected()
    {
        Assert.Throws<CutValidationException>(() =>
            Search(new CutQuery(new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 1))));
    }

    // ---------- Arama ----------

    [Fact]
    public void Lot_search_is_contains_and_ascii_case_insensitive()
    {
        CutAt(1, 8, "LOT-2026-A");
        CutAt(1, 9, "LOT-2026-B");
        CutAt(1, 10, "XYZ");

        Assert.Equal(2, Search(new CutQuery(Search: "2026")).TotalCount);
        Assert.Equal(2, Search(new CutQuery(Search: "lot-2026")).TotalCount);
        Assert.Equal(1, Search(new CutQuery(Search: "yz")).TotalCount);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData(@"\")]
    public void Like_wildcards_are_searched_literally(string wildcard)
    {
        CutAt(1, 8, "ABC");
        CutAt(1, 9, $"A{wildcard}C");

        var r = Search(new CutQuery(Search: wildcard));
        Assert.Equal($"A{wildcard}C", Assert.Single(r.Items).LotCode);
    }

    [Theory]
    [InlineData("K1-000002")]
    [InlineData("K1000002")]
    [InlineData("k1000002")]
    public void Id_search_matches_exactly(string text)
    {
        CutAt(1, 8);
        CutAt(1, 9);
        CutAt(1, 10);

        Assert.Equal("K1-000002", Assert.Single(Search(new CutQuery(Search: text)).Items).CutId);
    }

    // ---------- Durum ve özet ----------

    [Fact]
    public void Status_filter_and_summary()
    {
        CutAt(1, 8, cm: 1000);
        var b = CutAt(1, 9, cm: 2000);
        CutAt(1, 10, cm: 3050);
        _service.VoidCut(b.CutId, "hata");

        var all = Search(new CutQuery());
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(2, all.ActiveCount);
        Assert.Equal(1, all.VoidedCount);
        Assert.Equal(4050, all.ActiveLengthCm);   // iptal edilen sayılmaz

        Assert.Equal(2, Search(new CutQuery(Status: CutStatusFilter.Active)).TotalCount);
        Assert.Equal(b.CutId, Assert.Single(Search(new CutQuery(Status: CutStatusFilter.Voided)).Items).CutId);
    }

    [Fact]
    public void Empty_result_has_zero_summary()
    {
        var r = Search(new CutQuery(Search: "yok"));
        Assert.Empty(r.Items);
        Assert.Equal(0, r.TotalCount);
        Assert.Equal(0, r.ActiveLengthCm);
        Assert.Equal(1, r.PageCount);
        Assert.Equal(0, r.FirstRow);
    }

    // ---------- Sayfalama ----------

    [Fact]
    public void Paging_returns_newest_first_without_overlap()
    {
        for (var i = 0; i < 7; i++) CutAt(1, 8 + i);

        var p0 = Search(new CutQuery(PageSize: 3, Page: 0));
        var p1 = Search(new CutQuery(PageSize: 3, Page: 1));
        var p2 = Search(new CutQuery(PageSize: 3, Page: 2));

        Assert.Equal(["K1-000007", "K1-000006", "K1-000005"], p0.Items.Select(c => c.CutId));
        Assert.Equal(["K1-000004", "K1-000003", "K1-000002"], p1.Items.Select(c => c.CutId));
        Assert.Equal(["K1-000001"], p2.Items.Select(c => c.CutId));
        Assert.Equal(3, p0.PageCount);
        Assert.Equal((4L, 6L), (p1.FirstRow, p1.LastRow));
    }

    // ---------- Hız: 100.000 kayıt ----------

    [Fact]
    public void Search_100k_rows_under_one_second()
    {
        // 100.000 kesim, yaklaşık 1 yıla yayılmış (5 dakikada bir)
        var nowUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var sw = Stopwatch.StartNew();
        BulkData.Insert(_db.Factory, 100_000, nowUtc, TimeSpan.FromMinutes(5));
        _output.WriteLine($"Toplu kayıt: {sw.ElapsedMilliseconds} ms");

        var today = DateOnly.FromDateTime(nowUtc);
        var cases = new (string Name, CutQuery Query)[]
        {
            ("Son 7 gün",                 new(today.AddDays(-6), today)),
            ("Tümü, ilk sayfa",           new()),
            ("Tümü, son sayfa",           new(Page: 499)),
            ("Tümü, lot içerir",          new(Search: "LOT-012")),
            ("Tümü, ID",                  new(Search: "K1050000")),
            ("Tümü, yalnız iptal",        new(Status: CutStatusFilter.Voided)),
            ("1 yıl, lot + aktif",        new(today.AddYears(-1), today, "0042", CutStatusFilter.Active)),
        };

        Search(new CutQuery());   // ısınma (ilk bağlantı, sorgu planı)

        foreach (var (name, query) in cases)
        {
            sw.Restart();
            var r = Search(query);
            var ms = sw.Elapsed.TotalMilliseconds;
            _output.WriteLine($"{name,-22} {ms,7:0.0} ms  → {r.TotalCount} kayıt");
            Assert.True(ms < 1000, $"{name}: {ms:0} ms (sınır 1000 ms)");
        }

        Assert.Equal(100_000, Search(new CutQuery()).TotalCount);
        Assert.Equal(1, Search(new CutQuery(Search: "K1050000")).TotalCount);
    }

    /// <summary>
    /// ~1 milyon kayıt ölçümü (tek istasyonun tüm ID'leri). Uzun sürdüğü için yalnızca
    /// KUMASBARKOD_BENCH=1 ortam değişkeniyle çalışır; aksi hâlde hemen geçer.
    /// </summary>
    [Fact]
    public void Benchmark_1m_rows_on_request()
    {
        if (Environment.GetEnvironmentVariable("KUMASBARKOD_BENCH") != "1") return;

        var nowUtc = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var sw = Stopwatch.StartNew();
        // Tek istasyonun alabileceği en fazla ID: 999.999 (6 hane)
        BulkData.Insert(_db.Factory, (int)CutIds.MaxSequence, nowUtc, TimeSpan.FromMinutes(5));
        _output.WriteLine($"Toplu kayıt: {sw.ElapsedMilliseconds} ms, dosya {new FileInfo(_db.Factory.DatabasePath).Length / 1024 / 1024} MB (+WAL)");

        var today = DateOnly.FromDateTime(nowUtc);
        Search(new CutQuery());
        foreach (var (name, query) in new (string, CutQuery)[]
        {
            ("Son 7 gün",           new(today.AddDays(-6), today)),
            ("Tümü, ilk sayfa",     new()),
            ("Tümü, son sayfa",     new(Page: 4999)),
            ("Tümü, lot içerir",    new(Search: "LOT-012")),
            ("Tümü, ID",            new(Search: "K1500000")),
            ("Son 30 gün, lot",     new(today.AddDays(-29), today, "0042")),
        })
        {
            sw.Restart();
            var r = Search(query);
            _output.WriteLine($"{name,-20} {sw.Elapsed.TotalMilliseconds,8:0.0} ms  → {r.TotalCount} kayıt");
        }
    }

    private sealed class SettableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
