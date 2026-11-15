using KumasBarkod.Core.Data;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Sync;

namespace KumasBarkod.Tests.Data;

/// <summary>Faz 3: QC'deki lot önbelleği ve eşleştirme (sunucudaki kuralla aynı).</summary>
public class LotCatalogTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly LotCatalog _catalog;

    public LotCatalogTests()
    {
        DatabaseMigrator.Migrate(_db.Factory);
        _catalog = new LotCatalog(_db.Factory);
    }

    public void Dispose() => _db.Dispose();

    private static LotInfo Lot(long id, string name, bool active = true) => new(id, name, active, 0, 0, 0);

    private static IReadOnlyList<CatalogLot> Lots(params string[] names) =>
        names.Select((n, i) => new CatalogLot(i + 1, n, Core.Domain.LotKeys.Key(n))).ToList();

    [Fact]
    public void Replace_keeps_only_latest_list_and_active_lots()
    {
        _catalog.Replace([Lot(1, "LOT-3"), Lot(2, "Eski Firma", active: false), Lot(3, "Akın Tekstil")]);
        _catalog.Replace([Lot(1, "LOT-3"), Lot(3, "Akın Tekstil"), Lot(4, "YENİ")]);

        var active = _catalog.GetActive();
        Assert.Equal(["Akın Tekstil", "LOT-3", "YENİ"], active.Select(l => l.Name));
        Assert.Equal("AKINTEKSTIL", active[0].Key);
    }

    [Fact]
    public void Empty_catalog_means_no_check()
    {
        Assert.Equal(LotCheckKind.NoCatalog, LotMatcher.Check("ne olursa", []).Kind);
        Assert.Empty(LotMatcher.Suggest("LOT", []));
    }

    [Theory]
    [InlineData("LOT-3", "LOT-3")]
    [InlineData("lot 3", "LOT-3")]
    [InlineData("akin tekstil", "Akın Tekstil")]
    public void Spelling_difference_is_the_defined_lot(string typed, string expected)
    {
        var r = LotMatcher.Check(typed, Lots("LOT-3", "Akın Tekstil", "LOT-30"));
        Assert.Equal(LotCheckKind.Defined, r.Kind);
        Assert.Equal(expected, r.Match!.Name);
    }

    [Fact]
    public void Leading_zero_difference_is_only_a_suggestion()
    {
        var r = LotMatcher.Check("LOT-03", Lots("LOT-3", "LOT-30"));
        Assert.Equal(LotCheckKind.Undefined, r.Kind);
        Assert.Equal("LOT-3", r.Suggestion!.Name);
    }

    [Fact]
    public void Unrelated_lot_has_no_suggestion()
    {
        var r = LotMatcher.Check("LOT-31", Lots("LOT-3", "LOT-30"));
        Assert.Equal(LotCheckKind.Undefined, r.Kind);
        Assert.Null(r.Suggestion);   // "içinde geçiyor" önerilmez: yanlış firmaya bağlama riski
    }

    [Fact]
    public void Suggest_while_typing_prefix_first()
    {
        var lots = Lots("ALFA LOT", "LOT-3", "LOT-30", "Yıldız", "BETA");
        Assert.Equal(["LOT-3", "LOT-30", "ALFA LOT"], LotMatcher.Suggest("lot", lots));
        Assert.Equal(["Yıldız"], LotMatcher.Suggest("yil", lots));
        Assert.Empty(LotMatcher.Suggest("  ", lots));
        Assert.Equal(2, LotMatcher.Suggest("LOT", lots, max: 2).Count);
    }
}
