using Dapper;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;

namespace KumasBarkod.Core.Data;

/// <summary>Önbellekteki etkin bir lot (= firma).</summary>
public sealed record CatalogLot(long LotId, string Name, string Key);

/// <summary>
/// Depo sunucusundaki lot tanımlarının bu bilgisayardaki kopyası (<c>lots_cache</c>). Sunucuya her
/// gönderim turunda yenilenir; internet/sunucu yokken son bilinen liste kullanılır. Yalnızca öneri ve
/// uyarı içindir: kesim kaydı bu listeye BAĞLI DEĞİLDİR (liste boşsa ekran eskisi gibi çalışır).
/// </summary>
public sealed class LotCatalog(SqliteConnectionFactory db)
{
    public IReadOnlyList<CatalogLot> GetActive()
    {
        using var c = db.Open();
        return c.Query<(long Id, string Name, string Key)>(
                "SELECT lot_id, name, lot_key FROM lots_cache WHERE active = 1 ORDER BY name COLLATE NOCASE")
            .Select(x => new CatalogLot(x.Id, x.Name, x.Key)).ToList();
    }

    /// <summary>Listeyi tek işlemde yeniler (yarım liste kalmaz).</summary>
    public void Replace(IReadOnlyList<LotInfo> lots)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        c.Execute("DELETE FROM lots_cache", transaction: tx);
        c.Execute("INSERT INTO lots_cache (lot_id, name, lot_key, active) VALUES (@LotId, @Name, @Key, @Active)",
            lots.Select(l => new { l.LotId, l.Name, Key = LotKeys.Key(l.Name), Active = l.Active ? 1 : 0 }), tx);
        tx.Commit();
    }
}

public enum LotCheckKind
{
    /// <summary>Hiç lot tanımı yok (sunucu yok ya da henüz tanımlanmadı): kontrol yapılmaz.</summary>
    NoCatalog,
    /// <summary>Tanımlı lot (yazım farkı olabilir; <see cref="LotCheck.Match"/>.Name kullanılır).</summary>
    Defined,
    /// <summary>Tanımlı değil. <see cref="LotCheck.Suggestion"/>: yalnızca sayı sıfırı farkıyla uyan lot (varsa).</summary>
    Undefined,
}

public sealed record LotCheck(LotCheckKind Kind, CatalogLot? Match, CatalogLot? Suggestion);

/// <summary>Kalite kontrolde yazılan lot adını tanımlı lotlarla karşılaştırır (sunucudaki kuralla aynı: <see cref="LotKeys"/>).</summary>
public static class LotMatcher
{
    public static LotCheck Check(string lot, IReadOnlyList<CatalogLot> active)
    {
        if (active.Count == 0) return new LotCheck(LotCheckKind.NoCatalog, null, null);
        var key = LotKeys.Key(lot);
        if (active.FirstOrDefault(l => l.Key == key) is { } match) return new LotCheck(LotCheckKind.Defined, match, null);
        var loose = LotKeys.LooseKey(lot);
        var suggestion = active.FirstOrDefault(l => LotKeys.LooseKey(l.Name) == loose);
        return new LotCheck(LotCheckKind.Undefined, null, suggestion);
    }

    /// <summary>Yazılmaya başlanan metne uyan tanımlı lotlar: önce başı uyanlar, sonra içinde geçenler.</summary>
    public static IReadOnlyList<string> Suggest(string typed, IReadOnlyList<CatalogLot> active, int max = 8)
    {
        var key = LotKeys.Key(typed);
        if (key.Length == 0) return [];
        return active
            .Select(l => (l.Name, rank: l.Key.StartsWith(key, StringComparison.Ordinal) ? 0 : l.Key.Contains(key, StringComparison.Ordinal) ? 1 : -1))
            .Where(x => x.rank >= 0)
            .OrderBy(x => x.rank).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }
}
