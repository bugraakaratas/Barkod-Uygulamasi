using System.Globalization;
using System.Net.Http.Json;
using Dapper;
using KumasBarkod.Core.ServerApi;
using static KumasBarkod.Server.Tests.Rows;

namespace KumasBarkod.Server.Tests;

/// <summary>Faz 5: onayı geri alma, istisnalar (karantina çözümü), raporlar.</summary>
public sealed partial class ShipmentTests
{
    private Task<HttpResponseMessage> Reverse(HttpClient c, long id, string reason = "Yanlışlıkla onaylandı", string? requestId = null) =>
        Post(c, $"{ShipmentPaths.Shipments}/{id}/reverse", new ShipmentReverseRequest(reason, requestId ?? Guid.NewGuid().ToString("N")));

    private async Task<long> ConfirmedShipment(HttpClient c, long lot, params string[] barcodes)
    {
        var s = await OpenShipment(c, lot);
        ScanResponse? last = null;
        foreach (var b in barcodes) last = await Scan(c, s.ShipmentId, b);
        await Ok<ShipmentDetail>(await Confirm(c, s.ShipmentId, last!.ShipmentVersion));
        return s.ShipmentId;
    }

    private static string Today() => TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Local)
        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---------- Onayı geri alma ----------

    [Fact]
    public async Task Supervisor_reverses_confirmed_shipment_and_rolls_return_to_stock()
    {
        var (boss, ali, _) = await People();
        var (lot3, _) = await Stock();
        var id = await ConfirmedShipment(ali, lot3, "K1000001", "K1000002");

        Assert.Equal("FORBIDDEN", (await Err(await Reverse(ali, id), 403)).Code);   // depo personeli geri alamaz
        Assert.Equal(400, (int)(await Reverse(boss, id, "x")).StatusCode);           // sebep zorunlu

        var requestId = Guid.NewGuid().ToString("N");
        var d = await Ok<ShipmentDetail>(await Reverse(boss, id, "Tır gitmedi, yanlışlıkla onaylandı", requestId));

        Assert.Equal(ShipmentStatuses.Reversed, d.Status);
        Assert.Equal("Patron", d.ReversedBy);
        Assert.Equal("Tır gitmedi, yanlışlıkla onaylandı", d.ReversalReason);
        Assert.Equal("Ali", d.ClosedBy);   // onay bilgisi kalır
        Assert.Equal("in_stock", State("K1-000001"));
        Assert.Equal(2, Scalar("SELECT count(*) FROM stock_movements WHERE kind = 'ship_reversed'"));
        await Ok<ShipmentDetail>(await Reverse(boss, id, "Tır gitmedi, yanlışlıkla onaylandı", requestId));   // tekrar: sorun yok
        Assert.Equal(2, Scalar("SELECT count(*) FROM stock_movements WHERE kind = 'ship_reversed'"));
        Assert.Equal("SHIPMENT_NOT_CONFIRMED", (await Err(await Reverse(boss, id), 409)).Code);

        // Geri alınan top yeniden sevk edilebilir
        var again = await OpenShipment(ali, lot3);
        Assert.True((await Scan(ali, again.ShipmentId, "K1000001")).Accepted);
    }

    [Fact]
    public async Task Open_or_cancelled_shipment_cannot_be_reversed()
    {
        var (boss, ali, _) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);
        Assert.Equal("SHIPMENT_NOT_CONFIRMED", (await Err(await Reverse(boss, s.ShipmentId), 409)).Code);
        await Ok<ShipmentDetail>(await Cancel(ali, s.ShipmentId));
        Assert.Equal("SHIPMENT_NOT_CONFIRMED", (await Err(await Reverse(boss, s.ShipmentId), 409)).Code);
    }

    [Fact]
    public async Task Roll_voided_in_qc_after_shipping_is_listed_and_goes_to_voided_on_reversal()
    {
        var (boss, ali, _) = await People();
        var (lot3, _) = await Stock();
        var id = await ConfirmedShipment(ali, lot3, "K1000003");
        await _server.PostOk(Batch(Pc, "K1", Voided(Cut("K1", 3, lot: "LOT-3", cm: 300))));
        Assert.Equal("shipped", State("K1-000003"));   // tıra gitmiş top sessizce iptal görünmez

        var ex = (await boss.GetFromJsonAsync<ExceptionsResponse>("/" + ReportPaths.Exceptions, ServerApiContract.Json))!;
        var v = Assert.Single(ex.VoidedAfterShip);
        Assert.Equal(("K1-000003", "Çift kayıt"), (v.CutId, v.VoidReason));

        await Ok<ShipmentDetail>(await Reverse(boss, id));
        Assert.Equal("voided", State("K1-000003"));
        Assert.Empty((await boss.GetFromJsonAsync<ExceptionsResponse>("/" + ReportPaths.Exceptions, ServerApiContract.Json))!.VoidedAfterShip);
    }

    // ---------- İstisnalar: karantina ----------

    [Fact]
    public async Task Quarantine_is_listed_resolved_and_station_warning_clears()
    {
        var (boss, ali, _) = await People();
        await Stock();
        var duplicate = Cut("K1", 4, lot: "LOT-9", cm: 999, created: "2026-11-15T11:00:00.000Z");
        var r = await _server.PostOk(Batch(Pc, "K1", duplicate));
        Assert.Single(r.Conflicts);
        Assert.Equal("quarantined", State("K1-000004"));

        Assert.Equal(403, (int)(await ali.GetAsync("/" + ReportPaths.Exceptions)).StatusCode);
        var ex = (await boss.GetFromJsonAsync<ExceptionsResponse>("/" + ReportPaths.Exceptions, ServerApiContract.Json))!;
        var q = Assert.Single(ex.Quarantined);
        Assert.Equal(("LOT-3", 400L, "LOT-9", 999L), (q.ExistingLot, q.ExistingLengthCm, q.IncomingLot, q.IncomingLengthCm));
        Assert.Equal(1, ex.UnmatchedLotCount);

        Assert.Equal(400, (int)(await Post(boss, $"{ReportPaths.ResolveConflict}/{q.ConflictId}/resolve", new ConflictResolveRequest(""))).StatusCode);
        var after = await Ok<ExceptionsResponse>(await Post(boss, $"{ReportPaths.ResolveConflict}/{q.ConflictId}/resolve",
            new ConflictResolveRequest("İkinci top yeniden etiketlendi: K1-000031")));

        Assert.Empty(after.Quarantined);
        Assert.Equal("in_stock", State("K1-000004"));
        var history = (await _anon.GetFromJsonAsync<RollDetail>("/" + DepotPaths.Rolls + "/K1000004", ServerApiContract.Json))!;
        Assert.Equal("released", history.Movements[^1].Kind);
        Assert.Contains("yeniden etiketlendi", history.Movements[^1].Note);

        // QC bilgisayarı kuyruğundaki eski kaydı (ve iptalini) tekrar gönderir: artık çakışma yok, top etkilenmez
        var resend = await _server.PostOk(Batch(Pc, "K1", Voided(duplicate)));
        Assert.Empty(resend.Conflicts);
        Assert.Equal("in_stock", State("K1-000004"));
        Assert.Equal(409, (int)(await Post(boss, $"{ReportPaths.ResolveConflict}/{q.ConflictId}/resolve", new ConflictResolveRequest("tekrar"))).StatusCode);

        var s = await OpenShipment(ali, await LotIdOf("LOT-3"));
        Assert.True((await Scan(ali, s.ShipmentId, "K1000004")).Accepted);   // karantinadan çıkan top sevk edilebilir
    }

    [Fact]
    public async Task Quarantined_reserved_roll_returns_to_stock_not_to_reserved()
    {
        var (boss, ali, _) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);
        await Scan(ali, s.ShipmentId, "K1000002");
        await _server.PostOk(Batch(Pc, "K1", Cut("K1", 2, lot: "LOT-3", cm: 1, created: "2026-11-15T12:00:00.000Z")));
        var q = (await boss.GetFromJsonAsync<ExceptionsResponse>("/" + ReportPaths.Exceptions, ServerApiContract.Json))!.Quarantined.Single();

        await Ok<ExceptionsResponse>(await Post(boss, $"{ReportPaths.ResolveConflict}/{q.ConflictId}/resolve", new ConflictResolveRequest("Yeniden etiketlendi")));

        Assert.Equal("in_stock", State("K1-000002"));
    }

    [Fact]
    public async Task Stale_open_shipments_and_recent_confirmed_are_listed()
    {
        var (boss, ali, _) = await People();
        var (lot3, lot2) = await Stock();
        await ConfirmedShipment(ali, lot3, "K1000001");
        var open = await OpenShipment(ali, lot2);

        Assert.Empty((await boss.GetFromJsonAsync<ExceptionsResponse>("/" + ReportPaths.Exceptions, ServerApiContract.Json))!.StaleShipments);
        _server.Clock.Advance(TimeSpan.FromHours(25));
        var boss2 = await Login("Y0001", "9999");   // oturum 12 saatte düştü
        var ex = (await boss2.GetFromJsonAsync<ExceptionsResponse>("/" + ReportPaths.Exceptions, ServerApiContract.Json))!;
        Assert.Equal(open.ShipmentNo, Assert.Single(ex.StaleShipments).ShipmentNo);
        Assert.Single(ex.RecentConfirmed);
    }

    // ---------- Raporlar ----------

    [Fact]
    public async Task Reports_are_computed_from_real_records()
    {
        var (boss, ali, _) = await People();
        var (lot3, lot2) = await Stock();
        await ConfirmedShipment(ali, lot3, "K1000001", "K1000002");          // bugün: 2 top, 300 cm
        _server.Clock.Advance(TimeSpan.FromDays(1));
        ali = await Login("P0001", "1234");
        var reversed = await ConfirmedShipment(ali, lot2, "K1000006");      // yarın, sonra geri alınır
        boss = await Login("Y0001", "9999");
        await Ok<ShipmentDetail>(await Reverse(boss, reversed, "Yanlışlıkla onaylandı"));
        var cancelled = await OpenShipment(ali, lot3);
        await Scan(ali, cancelled.ShipmentId, "K1000003");
        await Ok<ShipmentDetail>(await Cancel(ali, cancelled.ShipmentId));

        var stock = (await _anon.GetFromJsonAsync<List<StockByLotRow>>("/" + ReportPaths.StockByLot, ServerApiContract.Json))!;
        Assert.Equal((3L, 1200L), (stock.Single(s => s.Lot == "LOT-3").Count, stock.Single(s => s.Lot == "LOT-3").LengthCm));   // 3,4,5
        Assert.Equal(2, stock.Single(s => s.Lot == "LOT-2").Count);                         // geri alınan 6 stokta
        Assert.True(stock.Single(s => s.Lot == "bilinmeyen firma").Unmatched);

        var today = Today();
        var tomorrow = DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var q = $"?from={today}&to={tomorrow}";
        var daily = (await _anon.GetFromJsonAsync<List<DailyShipmentRow>>("/" + ReportPaths.DailyShipments + q, ServerApiContract.Json))!;
        Assert.Equal([new DailyShipmentRow(today, 1, 2, 300)], daily);   // geri alınan sayılmaz

        var shipped = (await _anon.GetFromJsonAsync<List<ShippedRollRow>>("/" + ReportPaths.ShippedRolls + q, ServerApiContract.Json))!;
        Assert.Equal(["K1-000001", "K1-000002"], shipped.Select(x => x.CutId));
        Assert.All(shipped, x => Assert.Equal("Ali", x.ShippedBy));

        var history = (await _anon.GetFromJsonAsync<List<ShipmentHistoryRow>>("/" + ReportPaths.ShipmentHistory + q, ServerApiContract.Json))!;
        Assert.Equal([ShipmentStatuses.Cancelled, ShipmentStatuses.Reversed, ShipmentStatuses.Confirmed], history.Select(h => h.Status));
        Assert.Contains("Geri alındı (Patron)", history[1].Note);
        Assert.StartsWith("İptal:", history[0].Note);

        Assert.Equal(400, (int)(await _anon.GetAsync("/" + ReportPaths.DailyShipments + "?from=2026-11-15&to=2026-11-01")).StatusCode);
        Assert.Equal(400, (int)(await _anon.GetAsync("/" + ReportPaths.DailyShipments + "?from=15.11.2026&to=2026-11-16")).StatusCode);
        Assert.Equal(400, (int)(await _anon.GetAsync("/" + ReportPaths.DailyShipments + "?from=2024-01-01&to=2026-11-16")).StatusCode);
    }

    private async Task<long> LotIdOf(string name) =>
        (await _anon.GetFromJsonAsync<List<LotInfo>>("/" + DepotPaths.Lots, ServerApiContract.Json))!.Single(l => l.Name == name).LotId;
}
