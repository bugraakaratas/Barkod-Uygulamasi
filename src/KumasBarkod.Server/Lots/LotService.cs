using System.Globalization;
using Dapper;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Core.Services;
using KumasBarkod.Server.Data;
using KumasBarkod.Server.Ingest;
using Microsoft.Data.Sqlite;

namespace KumasBarkod.Server.Lots;

/// <summary>Hata kodları (lot işlemleri).</summary>
public static class LotErrorCodes
{
    public const string Exists = "LOT_EXISTS";
    public const string NotFound = "LOT_NOT_FOUND";
    public const string InvalidName = "LOT_INVALID_NAME";
    public const string RawNotFound = "LOT_RAW_NOT_FOUND";
}

/// <summary>
/// Lot (= firma) tanımları ve QC'de yazılan lot metinlerinin lotlara bağlanması.
/// Kurallar:
/// <list type="bullet">
/// <item>Lot adı benzersizdir (yazım farkı dahil: "LOT-3" varken "lot 3" eklenemez).</item>
/// <item>Top, QC'de yazılan metin değiştirilmeden lota BAĞLANIR (<c>rolls.lot_id</c>); etiket ve kayıt aynı kalır.</item>
/// <item>Bağlama sırası: elle eşleştirme (<c>lot_aliases</c>) → yazım farkı eşitliği (<see cref="LotKeys.Key"/>).
/// Yalnızca sayı sıfırı farkı (LOT-03 / LOT-3) kendiliğinden bağlanmaz, öneri olarak gösterilir.</item>
/// <item>Her bağlama değişikliği topun hareket geçmişine <c>lot_assigned</c> olarak yazılır.</item>
/// <item>Lot silinmez; pasif yapılır (yeni okutmalarda/seçimlerde görünmez, eski toplar bağlı kalır).</item>
/// </list>
/// </summary>
public sealed class LotService(SqliteConnectionFactory db, TimeProvider clock)
{
    public IReadOnlyList<LotInfo> List()
    {
        using var c = db.Open();
        return c.Query<(long LotId, string Name, long Active, long InStock, long InStockCm, long Total)>("""
            SELECT l.lot_id, l.name, l.active,
                   coalesce(sum(r.state = 'in_stock'), 0),
                   coalesce(sum(CASE WHEN r.state = 'in_stock' THEN r.length_cm END), 0),
                   count(r.cut_id)
            FROM lots l LEFT JOIN rolls r ON r.lot_id = l.lot_id
            GROUP BY l.lot_id
            ORDER BY l.active DESC, l.name COLLATE NOCASE
            """).Select(x => new LotInfo(x.LotId, x.Name, x.Active == 1, x.InStock, x.InStockCm, x.Total)).ToList();
    }

    public LotInfo Create(string? name, string? actor)
    {
        var clean = ValidateName(name);
        var key = LotKeys.Key(clean);
        var now = Now();

        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        EnsureKeyFree(c, tx, key, exceptLotId: null);

        var lotId = c.ExecuteScalar<long>("""
            INSERT INTO lots (name, lot_key, active, created_at, created_by, updated_at, updated_by)
            VALUES (@clean, @key, 1, @now, @actor, @now, @actor)
            RETURNING lot_id
            """, new { clean, key, now, actor }, tx);

        AssignUnmatchedByKey(c, tx, lotId, clean, key, now, actor);
        DataVersion.Bump(c, tx);
        tx.Commit();
        return Get(lotId);
    }

    public LotInfo Update(long lotId, LotUpdateRequest request, string? actor)
    {
        var now = Now();
        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        if (c.ExecuteScalar<long>("SELECT count(*) FROM lots WHERE lot_id = @lotId", new { lotId }, tx) == 0)
            throw NotFound(lotId);

        if (request.Name is not null)
        {
            var clean = ValidateName(request.Name);
            var key = LotKeys.Key(clean);
            EnsureKeyFree(c, tx, key, exceptLotId: lotId);
            c.Execute("UPDATE lots SET name = @clean, lot_key = @key, updated_at = @now, updated_by = @actor WHERE lot_id = @lotId",
                new { clean, key, now, actor, lotId }, tx);
            // Yeni ada yazım farkıyla uyan, henüz bağlanmamış toplar da bağlanır. Zaten bağlı toplar değişmez.
            AssignUnmatchedByKey(c, tx, lotId, clean, key, now, actor);
        }
        if (request.Active is { } active)
        {
            c.Execute("UPDATE lots SET active = @a, updated_at = @now, updated_by = @actor WHERE lot_id = @lotId",
                new { a = active ? 1 : 0, now, actor, lotId }, tx);
        }

        DataVersion.Bump(c, tx);
        tx.Commit();
        return Get(lotId);
    }

    /// <summary>
    /// QC'de yazılmış bir lot metnini lota bağlar (ör. "Lot 19" → "LOT-19"). Bu metni taşıyan bütün toplar
    /// (bugün ve gelecekte gelecek olanlar) bu lota bağlanır. Metin daha önce başka lota bağlandıysa
    /// düzeltilir: toplar yeni lota taşınır ve geçmişe yazılır.
    /// </summary>
    public LotAliasResponse MapRawText(long lotId, string? rawText, string? actor)
    {
        if (string.IsNullOrEmpty(rawText))
            throw new ApiException(400, LotErrorCodes.RawNotFound, "Lot metni boş.");
        var now = Now();

        using var c = db.Open();
        using var tx = c.BeginTransaction(deferred: false);
        var lotName = c.ExecuteScalar<string?>("SELECT name FROM lots WHERE lot_id = @lotId", new { lotId }, tx)
            ?? throw NotFound(lotId);
        if (c.ExecuteScalar<long>("SELECT count(*) FROM rolls WHERE lot_code_raw = @rawText", new { rawText }, tx) == 0)
            throw new ApiException(404, LotErrorCodes.RawNotFound, $"\"{rawText}\" lot metniyle kayıtlı top yok.");

        // Sevkiyatta okutulmuş / sevk edilmiş topun lotu değiştirilmez (Faz 4'te sevkiyat bu lota göre yapılmış olur).
        var locked = c.ExecuteScalar<long>("""
            SELECT count(*) FROM rolls WHERE lot_code_raw = @rawText AND state IN ('reserved', 'shipped')
              AND lot_id IS NOT NULL AND lot_id <> @lotId
            """, new { rawText, lotId }, tx);
        if (locked > 0)
            throw new ApiException(409, LotErrorCodes.Exists,
                $"\"{rawText}\" metinli {locked} top sevkiyatta ya da sevk edilmiş; lotu değiştirilemez.");

        c.Execute("""
            INSERT INTO lot_aliases (raw_text, lot_id, created_at, created_by) VALUES (@rawText, @lotId, @now, @actor)
            ON CONFLICT (raw_text) DO UPDATE SET lot_id = excluded.lot_id, created_at = excluded.created_at, created_by = excluded.created_by
            """, new { rawText, lotId, now, actor }, tx);

        var rolls = c.Query<(string CutId, long? LotId, string State)>(
            "SELECT cut_id, lot_id, state FROM rolls WHERE lot_code_raw = @rawText AND (lot_id IS NULL OR lot_id <> @lotId)",
            new { rawText, lotId }, tx).AsList();
        foreach (var r in rolls)
            AssignRoll(c, tx, r.CutId, r.State, r.LotId, lotId, lotName, now, actor, $"elle eşleştirildi ({actor ?? "bilinmiyor"})");

        DataVersion.Bump(c, tx);
        tx.Commit();
        return new LotAliasResponse(lotId, rawText, rolls.Count);
    }

    public IReadOnlyList<UnmatchedLot> Unmatched()
    {
        using var c = db.Open();
        var rows = c.Query<(string Raw, long Count, long InStock, long InStockCm, string First, string Last)>("""
            SELECT lot_code_raw, count(*), coalesce(sum(state = 'in_stock'), 0),
                   coalesce(sum(CASE WHEN state = 'in_stock' THEN length_cm END), 0),
                   min(qc_created_at), max(qc_created_at)
            FROM rolls WHERE lot_id IS NULL
            GROUP BY lot_code_raw
            ORDER BY max(qc_created_at) DESC
            """).AsList();
        var lots = c.Query<(long LotId, string Name, string Key)>("SELECT lot_id, name, lot_key FROM lots WHERE active = 1").AsList();

        return rows.Select(r => new UnmatchedLot(r.Raw, r.Count, r.InStock, r.InStockCm, r.First, r.Last, Suggest(r.Raw, lots))).ToList();
    }

    /// <summary>Gelen yeni top için lot: elle eşleştirme → yazım farkı eşitliği. Yoksa null (eşleşmemiş).</summary>
    public static long? Resolve(SqliteConnection c, SqliteTransaction tx, string rawText) =>
        c.ExecuteScalar<long?>("SELECT lot_id FROM lot_aliases WHERE raw_text = @rawText", new { rawText }, tx)
        ?? c.ExecuteScalar<long?>("SELECT lot_id FROM lots WHERE lot_key = @key", new { key = LotKeys.Key(rawText) }, tx);

    private static IReadOnlyList<LotSuggestion> Suggest(string raw, List<(long LotId, string Name, string Key)> lots)
    {
        var key = LotKeys.Key(raw);
        var loose = LotKeys.LooseKey(raw);
        return lots
            .Select(l => (l, score: l.Key == key ? 0
                : LotKeys.LooseKey(l.Name) == loose ? 1
                : key.Length >= 2 && (l.Key.Contains(key, StringComparison.Ordinal) || key.Contains(l.Key, StringComparison.Ordinal)) ? 2
                : -1))
            .Where(x => x.score >= 0)
            .OrderBy(x => x.score).ThenBy(x => x.l.Name, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(x => new LotSuggestion(x.l.LotId, x.l.Name))
            .ToList();
    }

    private void AssignUnmatchedByKey(SqliteConnection c, SqliteTransaction tx, long lotId, string lotName, string key,
        string now, string? actor)
    {
        // lot_code_raw için anahtar SQL'de hesaplanamaz (Türkçe harf katlama): metinler C#'ta karşılaştırılır.
        var raws = c.Query<string>("SELECT DISTINCT lot_code_raw FROM rolls WHERE lot_id IS NULL", transaction: tx)
            .Where(raw => LotKeys.Key(raw) == key)
            .ToList();
        foreach (var raw in raws)
        {
            // Elle eşleştirilmiş metin başka bir lota bağlıysa o karar geçerli
            if (c.ExecuteScalar<long?>("SELECT lot_id FROM lot_aliases WHERE raw_text = @raw", new { raw }, tx) is { } mapped && mapped != lotId)
                continue;
            foreach (var r in c.Query<(string CutId, string State)>(
                         "SELECT cut_id, state FROM rolls WHERE lot_id IS NULL AND lot_code_raw = @raw", new { raw }, tx).AsList())
                AssignRoll(c, tx, r.CutId, r.State, null, lotId, lotName, now, actor, "lot adıyla eşleşti");
        }
    }

    private static void AssignRoll(SqliteConnection c, SqliteTransaction tx, string cutId, string state, long? oldLotId,
        long lotId, string lotName, string now, string? actor, string reason)
    {
        c.Execute("UPDATE rolls SET lot_id = @lotId, updated_at = @now, version = version + 1 WHERE cut_id = @cutId",
            new { lotId, now, cutId }, tx);
        var oldName = oldLotId is null ? null
            : c.ExecuteScalar<string?>("SELECT name FROM lots WHERE lot_id = @oldLotId", new { oldLotId }, tx);
        var note = oldName is null ? $"Lota bağlandı: {lotName} — {reason}" : $"Lot değişti: {oldName} → {lotName} — {reason}";
        c.Execute("""
            INSERT INTO stock_movements (cut_id, kind, from_state, to_state, device_id, occurred_at, recorded_at, note)
            VALUES (@cutId, 'lot_assigned', @state, @state, @actor, @now, @now, @note)
            """, new { cutId, state, actor, now, note }, tx);
    }

    private static void EnsureKeyFree(SqliteConnection c, SqliteTransaction tx, string key, long? exceptLotId)
    {
        var existing = c.Query<(long LotId, string Name)>(
            "SELECT lot_id, name FROM lots WHERE lot_key = @key", new { key }, tx).ToList();
        if (existing.Count > 0 && existing[0] is var e && e.LotId != exceptLotId)
            throw new ApiException(409, LotErrorCodes.Exists, $"Bu lot zaten var: \"{e.Name}\" (yazım farkı sayılmaz).");
    }

    private static string ValidateName(string? name)
    {
        try
        {
            return CutService.NormalizeLot(name);
        }
        catch (CutValidationException ex)
        {
            throw new ApiException(400, LotErrorCodes.InvalidName, ex.Message);
        }
    }

    private LotInfo Get(long lotId) => List().Single(l => l.LotId == lotId);

    private static ApiException NotFound(long lotId) => new(404, LotErrorCodes.NotFound, $"Lot bulunamadı (no {lotId}).");

    private string Now() => Timestamps.Format(clock.GetUtcNow());
}

/// <summary>Veri sürümü sayacı (<c>server_settings.data_version</c>): değişiklik yapan her işlem aynı işlem içinde artırır.</summary>
public static class DataVersion
{
    public static void Bump(SqliteConnection c, SqliteTransaction tx) =>
        c.Execute("UPDATE server_settings SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT) WHERE key = 'data_version'",
            transaction: tx);

    public static long Get(SqliteConnection c, SqliteTransaction? tx = null) =>
        long.Parse(c.ExecuteScalar<string>("SELECT value FROM server_settings WHERE key = 'data_version'", transaction: tx)!,
            CultureInfo.InvariantCulture);
}
