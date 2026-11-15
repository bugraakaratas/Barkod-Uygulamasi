using System.Net.Http.Json;
using Dapper;
using KumasBarkod.Core.ServerApi;
using Microsoft.Data.Sqlite;
using static KumasBarkod.Server.Tests.Rows;

namespace KumasBarkod.Server.Tests;

/// <summary>Faz 2: lot (= firma) tanımları, lot metni eşleştirme, depo stoğu sorgusu, top geçmişi.</summary>
public sealed class LotAndStockTests : IDisposable
{
    private const string Pc1 = "PC-1";
    private readonly ServerFixture _server = new();
    private readonly HttpClient _http;

    public LotAndStockTests()
    {
        _http = _server.CreateClient();
        _http.DefaultRequestHeaders.Add(DepotPaths.DeviceHeader, "PATRON-PC");
    }

    public void Dispose()
    {
        Assert.Empty(_server.Invariants());
        _server.Dispose();
    }

    private async Task<LotInfo> CreateLot(string name)
    {
        using var r = await _http.PostAsJsonAsync("/" + DepotPaths.Lots, new LotCreateRequest(name), ServerApiContract.Json);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<LotInfo>(ServerApiContract.Json))!;
    }

    private async Task<ServerError> Error(HttpResponseMessage r, int status)
    {
        Assert.Equal(status, (int)r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ServerError>(ServerApiContract.Json))!;
    }

    private Task<StockResponse> Stock(string query = "") =>
        _http.GetFromJsonAsync<StockResponse>("/" + DepotPaths.Stock + query, ServerApiContract.Json)!;

    private Task<IReadOnlyList<UnmatchedLot>> Unmatched() =>
        _http.GetFromJsonAsync<IReadOnlyList<UnmatchedLot>>("/" + DepotPaths.UnmatchedLots, ServerApiContract.Json)!;

    private async Task<long> Version() =>
        (await _http.GetFromJsonAsync<DataVersionResponse>("/" + DepotPaths.Version, ServerApiContract.Json))!.DataVersion;

    private long? LotOf(string cutId)
    {
        using var c = _server.Open();
        return c.ExecuteScalar<long?>("SELECT lot_id FROM rolls WHERE cut_id = @cutId", new { cutId });
    }

    private List<(string Kind, string? Note)> Movements(string cutId)
    {
        using var c = _server.Open();
        return c.Query<(string, string?)>("SELECT kind, note FROM stock_movements WHERE cut_id = @cutId ORDER BY movement_id",
            new { cutId }).AsList();
    }

    // ---------- Lot tanımı ----------

    [Fact]
    public async Task Creating_lot_binds_existing_rolls_with_same_spelling_only()
    {
        await _server.PostOk(Batch(Pc1, "K1",
            Cut("K1", 1, lot: "LOT-3"), Cut("K1", 2, lot: "lot 3"), Cut("K1", 3, lot: "Lot_3", cm: 900),
            Cut("K1", 4, lot: "LOT-03"), Cut("K1", 5, lot: "LOT-31")));

        var lot = await CreateLot("LOT-3");

        Assert.Equal(3, lot.InStockCount);
        Assert.Equal(2550 + 2550 + 900, lot.InStockCm);
        Assert.Equal(lot.LotId, LotOf("K1-000002"));
        Assert.Null(LotOf("K1-000004"));   // sıfır farkı kendiliğinden bağlanmaz
        Assert.Null(LotOf("K1-000005"));
        var history = Movements("K1-000002");
        Assert.Equal("lot_assigned", history[^1].Kind);
        Assert.Contains("LOT-3", history[^1].Note);
    }

    [Fact]
    public async Task New_rolls_join_their_lot_on_arrival()
    {
        var lot = await CreateLot("Akın Tekstil");

        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, lot: "AKIN TEKSTİL"), Cut("K1", 2, lot: "akin-tekstil")));

        Assert.Equal(lot.LotId, LotOf("K1-000001"));
        Assert.Equal(lot.LotId, LotOf("K1-000002"));
        Assert.Single(Movements("K1-000001"));   // geldiğinde bağlandı: ayrı hareket yok
    }

    [Theory]
    [InlineData("lot 3")]
    [InlineData("LOT_3")]
    [InlineData("  LOT-3  ")]
    public async Task Duplicate_lot_with_different_spelling_is_rejected(string name)
    {
        await CreateLot("LOT-3");

        using var r = await _http.PostAsJsonAsync("/" + DepotPaths.Lots, new LotCreateRequest(name), ServerApiContract.Json);
        var e = await Error(r, 409);

        Assert.Equal("LOT_EXISTS", e.Code);
        Assert.Contains("LOT-3", e.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("çok uzun lot adı çok uzun lot adı çok uzun lot adı xx")]
    public async Task Invalid_lot_name_is_rejected(string name)
    {
        using var r = await _http.PostAsJsonAsync("/" + DepotPaths.Lots, new LotCreateRequest(name), ServerApiContract.Json);
        Assert.Equal("LOT_INVALID_NAME", (await Error(r, 400)).Code);
    }

    [Fact]
    public async Task Rename_checks_duplicates_and_binds_matching_unmatched_rolls()
    {
        var a = await CreateLot("LOT-A");
        await CreateLot("LOT-B");
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, lot: "Yıldız")));

        using (var dup = await _http.PutAsJsonAsync($"/{DepotPaths.Lots}/{a.LotId}", new LotUpdateRequest("lot b", null), ServerApiContract.Json))
            await Error(dup, 409);

        using var ok = await _http.PutAsJsonAsync($"/{DepotPaths.Lots}/{a.LotId}", new LotUpdateRequest("YILDIZ", null), ServerApiContract.Json);
        var renamed = (await ok.Content.ReadFromJsonAsync<LotInfo>(ServerApiContract.Json))!;

        Assert.Equal("YILDIZ", renamed.Name);
        Assert.Equal(a.LotId, LotOf("K1-000001"));
    }

    [Fact]
    public async Task Inactive_lot_still_receives_its_rolls_and_cannot_be_deleted()
    {
        var lot = await CreateLot("LOT-9");
        using (var r = await _http.PutAsJsonAsync($"/{DepotPaths.Lots}/{lot.LotId}", new LotUpdateRequest(null, false), ServerApiContract.Json))
            Assert.False((await r.Content.ReadFromJsonAsync<LotInfo>(ServerApiContract.Json))!.Active);

        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, lot: "LOT-9")));
        Assert.Equal(lot.LotId, LotOf("K1-000001"));

        using var c = _server.Open();
        var ex = Assert.Throws<SqliteException>(() => c.Execute("DELETE FROM lots"));
        Assert.Contains("silinemez", ex.Message);
    }

    [Fact]
    public async Task Unknown_lot_update_is_404()
    {
        using var r = await _http.PutAsJsonAsync($"/{DepotPaths.Lots}/999", new LotUpdateRequest("X", null), ServerApiContract.Json);
        Assert.Equal("LOT_NOT_FOUND", (await Error(r, 404)).Code);
    }

    // ---------- Eşleşmemiş lot metinleri ----------

    [Fact]
    public async Task Unmatched_texts_are_listed_with_suggestions()
    {
        await CreateLot("LOT-3");
        await CreateLot("LOT-30");
        await _server.PostOk(Batch(Pc1, "K1",
            Cut("K1", 1, lot: "LOT-003", created: "2026-11-15T08:00:00.000Z"),
            Cut("K1", 2, lot: "LOT-003", created: "2026-11-15T09:00:00.000Z", cm: 1000),
            Voided(Cut("K1", 3, lot: "hyk"))));

        var list = await Unmatched();

        var lot003 = list.Single(u => u.RawText == "LOT-003");
        Assert.Equal(2, lot003.RollCount);
        Assert.Equal(2, lot003.InStockCount);
        Assert.Equal(3550, lot003.InStockCm);
        Assert.Equal("LOT-3", lot003.Suggestions[0].Name);   // sıfır farkı: önerilir, bağlanmaz
        var hyk = list.Single(u => u.RawText == "hyk");
        Assert.Equal(0, hyk.InStockCount);
        Assert.Empty(hyk.Suggestions);
        Assert.Equal(2, (await Stock()).UnmatchedLotCount);
    }

    [Fact]
    public async Task Mapping_raw_text_binds_current_and_future_rolls()
    {
        var lot = await CreateLot("LOT-19");
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, lot: "Lot 19 yeni"), Cut("K1", 2, lot: "Lot 19 yeni")));

        using var r = await _http.PostAsJsonAsync($"/{DepotPaths.Lots}/{lot.LotId}/aliases", new LotAliasRequest("Lot 19 yeni"), ServerApiContract.Json);
        var mapped = (await r.Content.ReadFromJsonAsync<LotAliasResponse>(ServerApiContract.Json))!;

        Assert.Equal(2, mapped.AssignedRolls);
        Assert.Equal(lot.LotId, LotOf("K1-000001"));
        Assert.Contains("PATRON-PC", Movements("K1-000001")[^1].Note);
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 3, lot: "Lot 19 yeni")));
        Assert.Equal(lot.LotId, LotOf("K1-000003"));
        Assert.Empty(await Unmatched());
    }

    [Fact]
    public async Task Wrong_mapping_can_be_corrected_and_is_recorded()
    {
        var wrong = await CreateLot("LOT-1");
        var right = await CreateLot("LOT-7");
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, lot: "lot yedi")));
        await _http.PostAsJsonAsync($"/{DepotPaths.Lots}/{wrong.LotId}/aliases", new LotAliasRequest("lot yedi"), ServerApiContract.Json);

        using var r = await _http.PostAsJsonAsync($"/{DepotPaths.Lots}/{right.LotId}/aliases", new LotAliasRequest("lot yedi"), ServerApiContract.Json);

        Assert.Equal(1, (await r.Content.ReadFromJsonAsync<LotAliasResponse>(ServerApiContract.Json))!.AssignedRolls);
        Assert.Equal(right.LotId, LotOf("K1-000001"));
        var notes = Movements("K1-000001").Where(m => m.Kind == "lot_assigned").Select(m => m.Note).ToList();
        Assert.Equal(2, notes.Count);
        Assert.Contains("LOT-1 → LOT-7", notes[1]);
    }

    [Fact]
    public async Task Mapping_unknown_text_is_404()
    {
        var lot = await CreateLot("LOT-1");
        using var r = await _http.PostAsJsonAsync($"/{DepotPaths.Lots}/{lot.LotId}/aliases", new LotAliasRequest("yok böyle"), ServerApiContract.Json);
        Assert.Equal("LOT_RAW_NOT_FOUND", (await Error(r, 404)).Code);
    }

    [Fact]
    public async Task Manual_mapping_wins_over_spelling_match()
    {
        var a = await CreateLot("LOT-A");
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, lot: "LOT B")));
        await _http.PostAsJsonAsync($"/{DepotPaths.Lots}/{a.LotId}/aliases", new LotAliasRequest("LOT B"), ServerApiContract.Json);

        // Sonradan "LOT-B" tanımlanınca elle eşleştirilmiş "LOT B" metni LOT-A'da kalır
        var b = await CreateLot("LOT-B");
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 2, lot: "LOT B"), Cut("K1", 3, lot: "LOT-B")));

        Assert.Equal(a.LotId, LotOf("K1-000001"));
        Assert.Equal(a.LotId, LotOf("K1-000002"));
        Assert.Equal(b.LotId, LotOf("K1-000003"));
    }

    // ---------- Stok sorgusu ----------

    [Fact]
    public async Task Stock_counts_come_from_real_rolls()
    {
        var lot = await CreateLot("LOT-3");
        await _server.PostOk(Batch(Pc1, "K1",
            Cut("K1", 1, lot: "LOT-3", cm: 1000), Cut("K1", 2, lot: "LOT-3", cm: 2000), Voided(Cut("K1", 3, lot: "LOT-3", cm: 500)),
            Cut("K1", 4, lot: "başka", cm: 700)));

        var all = await Stock();
        Assert.Equal(3, all.InStock.Count);
        Assert.Equal(3700, all.InStock.LengthCm);
        Assert.Equal(3, all.Filtered.Count);
        Assert.Equal(["K1-000004", "K1-000002", "K1-000001"], all.Rows.Select(r => r.CutId));   // aynı QC zamanı → ID azalan

        var byLot = await Stock($"?lotId={lot.LotId}");
        Assert.Equal(2, byLot.Filtered.Count);
        Assert.Equal(3000, byLot.Filtered.LengthCm);
        Assert.All(byLot.Rows, r => Assert.Equal("LOT-3", r.LotName));
        Assert.Equal(3, byLot.InStock.Count);   // genel stok filtreden bağımsız

        var voided = await Stock("?state=voided");
        Assert.Equal("K1-000003", Assert.Single(voided.Rows).CutId);
        Assert.Equal("Çift kayıt", voided.Rows[0].VoidReason);

        var unmatched = await Stock("?unmatched=true&state=all");
        Assert.Equal("K1-000004", Assert.Single(unmatched.Rows).CutId);
        Assert.Null(unmatched.Rows[0].LotId);
        Assert.Equal("başka", unmatched.Rows[0].LotRaw);

        Assert.Equal(4, (await Stock("?state=all")).Filtered.Count);
    }

    [Theory]
    [InlineData("K1000002", "K1-000002")]
    [InlineData("k1-000002", "K1-000002")]
    [InlineData("LOT", "K1-000002")]
    public async Task Stock_search_by_barcode_or_lot_text(string search, string expected)
    {
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, lot: "ABC"), Cut("K1", 2, lot: "LOT-5")));

        var r = await Stock("?search=" + Uri.EscapeDataString(search));

        Assert.Equal(expected, Assert.Single(r.Rows).CutId);
        Assert.Equal("K1000002", r.Rows[0].Barcode);
    }

    [Fact]
    public async Task Stock_search_treats_wildcards_literally()
    {
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, lot: "A%B"), Cut("K1", 2, lot: "AXB")));
        Assert.Equal("K1-000001", Assert.Single((await Stock("?search=" + Uri.EscapeDataString("%"))).Rows).CutId);
        Assert.Empty((await Stock("?search=_")).Rows);
    }

    [Fact]
    public async Task Stock_paging_and_bad_filter()
    {
        await _server.PostOk(Batch(Pc1, "K1", Enumerable.Range(1, 12).Select(i =>
            Cut("K1", i, created: $"2026-11-15T08:{i:D2}:00.000Z")).ToArray()));

        var page2 = await Stock("?limit=5&offset=5");
        Assert.Equal(12, page2.Filtered.Count);
        Assert.Equal(["K1-000007", "K1-000006", "K1-000005", "K1-000004", "K1-000003"], page2.Rows.Select(r => r.CutId));

        using var bad = await _http.GetAsync("/" + DepotPaths.Stock + "?state=kayip");
        await Error(bad, 400);
    }

    // ---------- Top geçmişi ----------

    [Fact]
    public async Task Roll_detail_shows_full_history_from_qc()
    {
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 7, lot: "Lot 7")));
        var lot = await CreateLot("LOT-7");
        await _http.PostAsJsonAsync($"/{DepotPaths.Lots}/{lot.LotId}/aliases", new LotAliasRequest("Lot 7"), ServerApiContract.Json);
        await _server.PostOk(Batch(Pc1, "K1", Voided(Cut("K1", 7, lot: "Lot 7"))));

        var detail = (await _http.GetFromJsonAsync<RollDetail>("/" + DepotPaths.Rolls + "/K1000007", ServerApiContract.Json))!;

        Assert.Equal("K1-000007", detail.Roll.CutId);
        Assert.Equal("LOT-7", detail.Roll.LotName);
        Assert.Equal("Lot 7", detail.Roll.LotRaw);
        Assert.Equal("voided", detail.Roll.State);
        Assert.Equal("K1", detail.StationCode);
        Assert.Equal(["qc_created", "lot_assigned", "qc_voided"], detail.Movements.Select(m => m.Kind));

        using var missing = await _http.GetAsync("/" + DepotPaths.Rolls + "/K1000099");
        Assert.Equal("ROLL_NOT_FOUND", (await Error(missing, 404)).Code);
    }

    // ---------- Veri sürümü ----------

    [Fact]
    public async Task Data_version_changes_only_when_data_changes()
    {
        var v0 = await Version();
        await _server.PostOk(Batch(Pc1, "K1"));                  // el sıkışma: veri değişmedi
        Assert.Equal(v0, await Version());

        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1)));
        var v1 = await Version();
        Assert.True(v1 > v0);

        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1)));    // aynı paket tekrar: değişiklik yok
        Assert.Equal(v1, await Version());

        await CreateLot("LOT-3");
        Assert.True(await Version() > v1);
        Assert.Equal(await Version(), (await Stock()).DataVersion);
    }
}
