using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using KumasBarkod.Core.ServerApi;
using Microsoft.Data.Sqlite;
using static KumasBarkod.Server.Tests.Rows;

namespace KumasBarkod.Server.Tests;

/// <summary>Faz 4: personel girişi ve barkodla sevkiyat (kullanıcının istediği 12 test senaryosu dahil).</summary>
public sealed class ShipmentTests : IDisposable
{
    private const string Pc = "PC-1";
    private readonly ServerFixture _server = new();
    private readonly HttpClient _anon;

    public ShipmentTests()
    {
        _anon = _server.CreateClient();
        _anon.DefaultRequestHeaders.Add(DepotPaths.DeviceHeader, "RIHTIM-PC");
    }

    public void Dispose()
    {
        Assert.Empty(_server.Invariants());
        _server.Dispose();
    }

    // ---------- Yardımcılar ----------

    private async Task<T> Ok<T>(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return System.Text.Json.JsonSerializer.Deserialize<T>(text, ServerApiContract.Json)!;
    }

    private static async Task<ServerError> Err(HttpResponseMessage r, int status)
    {
        Assert.Equal(status, (int)r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ServerError>(ServerApiContract.Json))!;
    }

    private Task<HttpResponseMessage> Post(HttpClient c, string path, object body) =>
        c.PostAsJsonAsync("/" + path, body, ServerApiContract.Json);

    /// <summary>Yetkili (ilk kurulum) + depo personeli; giriş yapmış istemciler.</summary>
    private async Task<(HttpClient Boss, HttpClient Ali, HttpClient Veli)> People()
    {
        await Ok<UserInfo>(await Post(_anon, ShipmentPaths.Users, new UserCreateRequest("Patron", UserRoles.Supervisor, "Y0001", "9999")));
        var boss = await Login("Y0001", "9999");
        await Ok<UserInfo>(await Post(boss, ShipmentPaths.Users, new UserCreateRequest("Ali", UserRoles.Warehouse, "P0001", "1234")));
        await Ok<UserInfo>(await Post(boss, ShipmentPaths.Users, new UserCreateRequest("Veli", UserRoles.Warehouse, "P0002", "5678")));
        return (boss, await Login("P0001", "1234"), await Login("P0002", "5678"));
    }

    private async Task<HttpClient> Login(string badge, string pin)
    {
        var r = await Ok<LoginResponse>(await Post(_anon, ShipmentPaths.Login, new LoginRequest(badge, pin)));
        var c = _server.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", r.Token);
        c.DefaultRequestHeaders.Add(DepotPaths.DeviceHeader, "RIHTIM-PC");
        return c;
    }

    private async Task<long> Lot(string name) =>
        (await Ok<LotInfo>(await Post(_anon, DepotPaths.Lots, new LotCreateRequest(name)))).LotId;

    /// <summary>LOT-3: K1-1..5 (100..500 cm), LOT-2: K1-6..7, eşleşmemiş: K1-8, iptal: K1-9.</summary>
    private async Task<(long Lot3, long Lot2)> Stock()
    {
        var lot3 = await Lot("LOT-3");
        var lot2 = await Lot("LOT-2");
        await _server.PostOk(Batch(Pc, "K1",
            Cut("K1", 1, lot: "LOT-3", cm: 100), Cut("K1", 2, lot: "LOT-3", cm: 200), Cut("K1", 3, lot: "LOT-3", cm: 300),
            Cut("K1", 4, lot: "LOT-3", cm: 400), Cut("K1", 5, lot: "LOT-3", cm: 500),
            Cut("K1", 6, lot: "LOT-2", cm: 600), Cut("K1", 7, lot: "LOT-2", cm: 700),
            Cut("K1", 8, lot: "bilinmeyen firma", cm: 800), Voided(Cut("K1", 9, lot: "LOT-3", cm: 900))));
        return (lot3, lot2);
    }

    private async Task<ShipmentDetail> OpenShipment(HttpClient c, params long[] lots) =>
        await Ok<ShipmentDetail>(await Post(c, ShipmentPaths.Shipments, new ShipmentOpenRequest(lots)));

    private async Task<ScanResponse> Scan(HttpClient c, long shipmentId, string raw, string? scanId = null) =>
        await Ok<ScanResponse>(await Post(c, $"{ShipmentPaths.Shipments}/{shipmentId}/scans", new ScanRequest(scanId ?? Guid.NewGuid().ToString("N"), raw)));

    private async Task<ScanResponse> Remove(HttpClient c, long shipmentId, string raw) =>
        await Ok<ScanResponse>(await Post(c, $"{ShipmentPaths.Shipments}/{shipmentId}/removals", new ScanRequest(Guid.NewGuid().ToString("N"), raw)));

    private Task<HttpResponseMessage> Confirm(HttpClient c, long id, long version, string? requestId = null) =>
        Post(c, $"{ShipmentPaths.Shipments}/{id}/confirm", new ShipmentConfirmRequest(version, requestId ?? Guid.NewGuid().ToString("N")));

    private Task<HttpResponseMessage> Cancel(HttpClient c, long id, string? requestId = null) =>
        Post(c, $"{ShipmentPaths.Shipments}/{id}/cancel", new ShipmentCancelRequest(CancelReasons.TruckNotCame, requestId ?? Guid.NewGuid().ToString("N")));

    private async Task<ShipmentDetail> Get(HttpClient c, long id) =>
        (await c.GetFromJsonAsync<ShipmentDetail>($"/{ShipmentPaths.Shipments}/{id}", ServerApiContract.Json))!;

    private string State(string cutId)
    {
        using var db = _server.Open();
        return db.ExecuteScalar<string>("SELECT state FROM rolls WHERE cut_id = @cutId", new { cutId })!;
    }

    private long Scalar(string sql)
    {
        using var db = _server.Open();
        return db.ExecuteScalar<long>(sql);
    }

    // ---------- Personel ve giriş ----------

    [Fact]
    public async Task First_supervisor_needs_no_login_then_login_is_required()
    {
        Assert.True((await _anon.GetFromJsonAsync<BootstrapNeededResponse>("/" + ShipmentPaths.BootstrapNeeded, ServerApiContract.Json))!.Needed);
        await Err(await Post(_anon, ShipmentPaths.Users, new UserCreateRequest("Ali", UserRoles.Warehouse, "P0001", "1234")), 400);   // ilk kişi yetkili olmalı

        await Ok<UserInfo>(await Post(_anon, ShipmentPaths.Users, new UserCreateRequest("Patron", UserRoles.Supervisor, "Y0001", "9999")));

        Assert.False((await _anon.GetFromJsonAsync<BootstrapNeededResponse>("/" + ShipmentPaths.BootstrapNeeded, ServerApiContract.Json))!.Needed);
        await Err(await Post(_anon, ShipmentPaths.Users, new UserCreateRequest("Sızma", UserRoles.Supervisor, "Y0002", "1111")), 401);
        var ali = await Login("Y0001", "9999");
        var created = await Ok<UserInfo>(await Post(ali, ShipmentPaths.Users, new UserCreateRequest("Ali", UserRoles.Warehouse, "p0001", "1234")));
        Assert.Equal("P0001", created.BadgeCode);   // kart kodu büyük harfe çevrilir
    }

    [Fact]
    public async Task Warehouse_user_cannot_manage_people()
    {
        var (_, ali, _) = await People();
        Assert.Equal("FORBIDDEN", (await Err(await Post(ali, ShipmentPaths.Users, new UserCreateRequest("X", UserRoles.Warehouse, "P0009", "1111")), 403)).Code);
        Assert.Equal(403, (int)(await ali.GetAsync("/" + ShipmentPaths.Users)).StatusCode);
    }

    [Theory]
    [InlineData("K1000123", "1234")]   // top barkoduna benzeyen kart
    [InlineData("P1", "1234")]         // çok kısa
    [InlineData("P0005", "12")]        // PIN kısa
    [InlineData("P0005", "12a4")]      // PIN rakam değil
    public async Task Invalid_user_is_rejected(string badge, string pin)
    {
        var (boss, _, _) = await People();
        Assert.Equal("USER_INVALID", (await Err(await Post(boss, ShipmentPaths.Users, new UserCreateRequest("X", UserRoles.Warehouse, badge, pin)), 400)).Code);
    }

    [Fact]
    public async Task Duplicate_badge_is_rejected()
    {
        var (boss, _, _) = await People();
        Assert.Equal("BADGE_EXISTS", (await Err(await Post(boss, ShipmentPaths.Users, new UserCreateRequest("X", UserRoles.Warehouse, "P0001", "1111")), 409)).Code);
    }

    [Fact]
    public async Task Wrong_pin_does_not_reveal_badge_and_locks_after_five_tries()
    {
        await People();
        var unknown = await Err(await Post(_anon, ShipmentPaths.Login, new LoginRequest("P9999", "1234")), 401);
        var wrong = await Err(await Post(_anon, ShipmentPaths.Login, new LoginRequest("P0001", "0000")), 401);
        Assert.Equal(unknown.Message, wrong.Message);   // kartın var olup olmadığı söylenmez

        for (var i = 0; i < 3; i++) await Err(await Post(_anon, ShipmentPaths.Login, new LoginRequest("P0001", "0000")), 401);
        Assert.Equal("LOCKED", (await Err(await Post(_anon, ShipmentPaths.Login, new LoginRequest("P0001", "0000")), 423)).Code);
        Assert.Equal("LOCKED", (await Err(await Post(_anon, ShipmentPaths.Login, new LoginRequest("P0001", "1234")), 423)).Code);   // doğru PIN de kilitli

        _server.Clock.Advance(TimeSpan.FromMinutes(6));
        await Login("p0001", "1234");   // kilit açıldı; küçük harfli kart da olur
    }

    [Fact]
    public async Task Deactivated_user_loses_session_and_last_supervisor_is_protected()
    {
        var (boss, ali, _) = await People();
        var users = (await boss.GetFromJsonAsync<List<UserInfo>>("/" + ShipmentPaths.Users, ServerApiContract.Json))!;
        var aliId = users.Single(u => u.Name == "Ali").UserId;
        var bossId = users.Single(u => u.Name == "Patron").UserId;

        await Ok<UserInfo>(await boss.PutAsJsonAsync($"/{ShipmentPaths.Users}/{aliId}", new UserUpdateRequest(null, null, false, null), ServerApiContract.Json));
        Assert.Equal(401, (int)(await ali.GetAsync("/" + ShipmentPaths.Shipments)).StatusCode);
        await Err(await Post(_anon, ShipmentPaths.Login, new LoginRequest("P0001", "1234")), 401);

        var e = await Err(await boss.PutAsJsonAsync($"/{ShipmentPaths.Users}/{bossId}", new UserUpdateRequest(null, UserRoles.Warehouse, null, null), ServerApiContract.Json), 400);
        Assert.Contains("Son etkin yetkili", e.Message);
    }

    [Fact]
    public async Task Session_expires_and_shipments_require_login()
    {
        var (_, ali, _) = await People();
        Assert.Equal(401, (int)(await _anon.GetAsync("/" + ShipmentPaths.Shipments)).StatusCode);
        Assert.Equal(200, (int)(await ali.GetAsync("/" + ShipmentPaths.Shipments)).StatusCode);
        _server.Clock.Advance(TimeSpan.FromHours(13));
        Assert.Equal(401, (int)(await ali.GetAsync("/" + ShipmentPaths.Shipments)).StatusCode);
    }

    [Fact]
    public void Production_pin_hashing_uses_100k_iterations() =>
        Assert.Equal(100_000, KumasBarkod.Server.Auth.AuthService.DefaultIterations);

    [Fact]
    public async Task Pin_hash_records_its_iteration_count()
    {
        await People();
        using var db = _server.Open();
        Assert.All(db.Query<string>("SELECT pin_hash FROM users"), h => Assert.StartsWith("1000:", h));
        Assert.DoesNotContain("1234", string.Join("", db.Query<string>("SELECT pin_hash || pin_salt FROM users")));
    }

    // ---------- Sevkiyat açma ----------

    [Fact]
    public async Task Opening_validates_lots_numbers_shipments_and_warns_about_shared_lot()
    {
        var (_, ali, veli) = await People();
        var (lot3, _) = await Stock();
        Assert.Equal("INVALID_LOTS", (await Err(await Post(ali, ShipmentPaths.Shipments, new ShipmentOpenRequest([])), 400)).Code);
        Assert.Equal("INVALID_LOTS", (await Err(await Post(ali, ShipmentPaths.Shipments, new ShipmentOpenRequest([999])), 400)).Code);

        var a = await OpenShipment(ali, lot3);
        var b = await OpenShipment(veli, lot3);

        Assert.Equal("SV-2026-00001", a.ShipmentNo);
        Assert.Equal("SV-2026-00002", b.ShipmentNo);
        Assert.Equal("Ali", a.OpenedBy);
        Assert.Equal(5, a.Stats.RemainingCount);
        Assert.Contains(b.Warnings, w => w.Contains("SV-2026-00001"));
        Assert.Equal(2, (await ali.GetFromJsonAsync<List<ShipmentSummary>>("/" + ShipmentPaths.Shipments + "?status=open", ServerApiContract.Json))!.Count);
    }

    // ---------- Senaryo 1–2: stoktaki doğru lot topu ----------

    [Fact]
    public async Task S1_S2_valid_barcode_of_selected_lot_is_reserved()
    {
        var (_, ali, _) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);

        var r = await Scan(ali, s.ShipmentId, "K1000002");

        Assert.True(r.Accepted);
        Assert.Equal((ScanCodes.Ok, ScanSeverity.Success), (r.Code, r.Severity));
        Assert.Equal("K1-000002", r.Roll!.CutId);
        Assert.Equal((1L, 200L, 4L, 1300L), (r.Stats.Count, r.Stats.LengthCm, r.Stats.RemainingCount, r.Stats.RemainingCm));
        Assert.Equal("reserved", State("K1-000002"));
        using var db = _server.Open();
        var m = db.QuerySingle<(string Kind, long Shipment, long User, string Device)>(
            "SELECT kind, shipment_id, user_id, device_id FROM stock_movements WHERE cut_id = 'K1-000002' ORDER BY movement_id DESC LIMIT 1");
        Assert.Equal(("reserved", s.ShipmentId, 2L, "RIHTIM-PC"), m);
    }

    // ---------- Senaryo 3: başka lot ----------

    [Fact]
    public async Task S3_barcode_of_other_lot_is_rejected_and_not_reserved()
    {
        var (_, ali, _) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);

        var r = await Scan(ali, s.ShipmentId, "K1000006");

        Assert.False(r.Accepted);
        Assert.Equal((ScanCodes.WrongLot, ScanSeverity.Error), (r.Code, r.Severity));
        Assert.Equal("Bu barkod seçili lota ait değil. Barkodun lotu: LOT-2. Seçili lot: LOT-3.", r.Message);
        Assert.Equal("in_stock", State("K1-000006"));
        Assert.Equal(0, r.Stats.Count);
        Assert.Equal(1, r.Stats.Rejected);
        var d = await Get(ali, s.ShipmentId);
        Assert.Equal(ScanCodes.WrongLot, d.RecentScans[0].Code);   // ret kaydedildi, sebebi okunabilir
        Assert.Equal(r.Message, d.RecentScans[0].Message);
    }

    // ---------- Senaryo 4: sistemde yok ----------

    [Theory]
    [InlineData("K1000099", ScanCodes.NotFound)]
    [InlineData("abc", ScanCodes.InvalidBarcode)]
    [InlineData("K1000008", ScanCodes.LotUnassigned)]
    [InlineData("K1000009", ScanCodes.Voided)]
    public async Task S4_unknown_invalid_unassigned_or_voided_is_rejected(string raw, string code)
    {
        var (_, ali, _) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);

        var r = await Scan(ali, s.ShipmentId, raw);

        Assert.False(r.Accepted);
        Assert.Equal((code, ScanSeverity.Error), (r.Code, r.Severity));
        Assert.Equal(0, Scalar("SELECT count(*) FROM shipment_items"));
    }

    // ---------- Senaryo 5: aynı barkod iki kez ----------

    [Fact]
    public async Task S5_same_barcode_twice_adds_once()
    {
        var (_, ali, veli) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);
        await Scan(ali, s.ShipmentId, "K1000001");

        var again = await Scan(veli, s.ShipmentId, "k1-000001");

        Assert.False(again.Accepted);
        Assert.Equal((ScanCodes.AlreadyInThisShipment, ScanSeverity.Warning), (again.Code, again.Severity));
        Assert.Contains("Ali", again.Message);
        Assert.Equal(1, again.Stats.Count);
        Assert.Equal(0, again.Stats.Rejected);   // tekrar okutma "reddedilen" sayılmaz (zararsız)
        Assert.Equal(1, Scalar("SELECT count(*) FROM shipment_items"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM stock_movements WHERE kind = 'reserved'"));
    }

    // ---------- Senaryo 6: daha önce sevk edilmiş ----------

    [Fact]
    public async Task S6_already_shipped_barcode_is_rejected_everywhere()
    {
        var (_, ali, veli) = await People();
        var (lot3, _) = await Stock();
        var first = await OpenShipment(ali, lot3);
        var r = await Scan(ali, first.ShipmentId, "K1000003");
        await Ok<ShipmentDetail>(await Confirm(ali, first.ShipmentId, r.ShipmentVersion));

        var second = await OpenShipment(veli, lot3);
        var again = await Scan(veli, second.ShipmentId, "K1000003");

        Assert.Equal((ScanCodes.AlreadyShipped, ScanSeverity.Error), (again.Code, again.Severity));
        Assert.Contains(first.ShipmentNo, again.Message);
        Assert.Equal("shipped", State("K1-000003"));
    }

    // ---------- Senaryo 7: iki kişi aynı barkodu aynı anda ----------

    [Fact]
    public async Task S7_two_people_scanning_same_barcode_at_once_only_one_wins()
    {
        var (_, ali, veli) = await People();
        var (lot3, _) = await Stock();
        var a = await OpenShipment(ali, lot3);
        var b = await OpenShipment(veli, lot3);

        var tasks = Enumerable.Range(0, 20).Select(i => i % 2 == 0
            ? Scan(ali, a.ShipmentId, "K1000004")
            : Scan(veli, b.ShipmentId, "K1000004")).ToList();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, r => r.Accepted);
        var winner = results.Single(r => r.Accepted);
        Assert.All(results.Where(r => !r.Accepted), r => Assert.Contains(r.Code,
            new[] { ScanCodes.AlreadyInThisShipment, ScanCodes.ReservedInOtherShipment }));
        Assert.Equal(1, Scalar("SELECT count(*) FROM shipment_items WHERE cut_id = 'K1-000004' AND status = 'active'"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM stock_movements WHERE cut_id = 'K1-000004' AND kind = 'reserved'"));
        Assert.Equal(20, Scalar("SELECT count(*) FROM scan_events"));   // her deneme kayıtlı
        Assert.True(winner.Stats.Count == 1);
    }

    [Fact]
    public async Task Same_scan_resent_after_network_loss_is_processed_once()
    {
        var (_, ali, _) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);
        var scanId = Guid.NewGuid().ToString("N");

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Scan(ali, s.ShipmentId, "K1000005", scanId)));

        Assert.All(results, r => Assert.True(r.Accepted));
        Assert.All(results, r => Assert.Equal(results[0].Message, r.Message));
        Assert.Equal(1, Scalar("SELECT count(*) FROM shipment_items"));
        Assert.Equal(1, Scalar("SELECT count(*) FROM scan_events"));
    }

    // ---------- Senaryo 8: iptal ----------

    [Fact]
    public async Task S8_cancel_returns_rolls_to_stock_and_keeps_history()
    {
        var (_, ali, _) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);
        await Scan(ali, s.ShipmentId, "K1000001");
        await Scan(ali, s.ShipmentId, "K1000002");
        var requestId = Guid.NewGuid().ToString("N");

        var cancelled = await Ok<ShipmentDetail>(await Cancel(ali, s.ShipmentId, requestId));

        Assert.Equal(ShipmentStatuses.Cancelled, cancelled.Status);
        Assert.Equal(CancelReasons.TruckNotCame, cancelled.CloseReason);
        Assert.Equal("in_stock", State("K1-000001"));
        Assert.Equal(5, cancelled.Stats.RemainingCount);
        Assert.Equal(2, Scalar("SELECT count(*) FROM shipment_items WHERE status = 'cancelled'"));
        Assert.Equal(2, Scalar("SELECT count(*) FROM stock_movements WHERE kind = 'unreserved'"));

        await Ok<ShipmentDetail>(await Cancel(ali, s.ShipmentId, requestId));   // aynı istek tekrar: sorun yok
        Assert.Equal("SHIPMENT_NOT_OPEN", (await Err(await Cancel(ali, s.ShipmentId), 409)).Code);
        var late = await Scan(ali, s.ShipmentId, "K1000003");
        Assert.Equal(ScanCodes.ShipmentNotOpen, late.Code);
        Assert.Equal("in_stock", State("K1-000003"));
    }

    // ---------- Senaryo 9–10: kısmi sevkiyat ve onay ----------

    [Fact]
    public async Task S9_S10_partial_shipment_confirms_only_scanned_rolls()
    {
        var (_, ali, _) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);
        await Scan(ali, s.ShipmentId, "K1000001");
        await Scan(ali, s.ShipmentId, "K1000002");
        var last = await Scan(ali, s.ShipmentId, "K1000003");

        var done = await Ok<ShipmentDetail>(await Confirm(ali, s.ShipmentId, last.ShipmentVersion));

        Assert.Equal(ShipmentStatuses.Confirmed, done.Status);
        Assert.Equal("Ali", done.ClosedBy);
        Assert.Equal((3L, 600L, 2L, 900L), (done.Stats.Count, done.Stats.LengthCm, done.Stats.RemainingCount, done.Stats.RemainingCm));
        Assert.Equal(["shipped", "shipped", "shipped", "in_stock", "in_stock"],
            new[] { "K1-000001", "K1-000002", "K1-000003", "K1-000004", "K1-000005" }.Select(State));
        Assert.Equal(3, Scalar("SELECT count(*) FROM stock_movements WHERE kind = 'shipped'"));
    }

    [Fact]
    public async Task Confirm_checks_version_emptiness_and_is_idempotent()
    {
        var (_, ali, veli) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);
        Assert.Equal("SHIPMENT_EMPTY", (await Err(await Confirm(ali, s.ShipmentId, s.Version), 400)).Code);

        var mine = await Scan(ali, s.ShipmentId, "K1000001");
        await Scan(veli, s.ShipmentId, "K1000002");   // başka cihaz araya okuttu
        var stale = await Err(await Confirm(ali, s.ShipmentId, mine.ShipmentVersion), 409);
        Assert.Equal("VERSION_MISMATCH", stale.Code);
        Assert.Equal("reserved", State("K1-000001"));

        var fresh = await Get(ali, s.ShipmentId);
        var requestId = Guid.NewGuid().ToString("N");
        await Ok<ShipmentDetail>(await Confirm(ali, s.ShipmentId, fresh.Version, requestId));
        var again = await Ok<ShipmentDetail>(await Confirm(ali, s.ShipmentId, fresh.Version, requestId));   // yanıt kayboldu, tekrar
        Assert.Equal(ShipmentStatuses.Confirmed, again.Status);
        Assert.Equal(2, Scalar("SELECT count(*) FROM stock_movements WHERE kind = 'shipped'"));
        Assert.Equal("SHIPMENT_NOT_OPEN", (await Err(await Cancel(ali, s.ShipmentId), 409)).Code);
    }

    // ---------- Senaryo 11: yarım kalan sevkiyat ----------

    [Fact]
    public async Task S11_unfinished_shipment_can_be_resumed_by_anyone_and_closed()
    {
        var (_, ali, veli) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);
        await Scan(ali, s.ShipmentId, "K1000001");

        // Ali'nin bilgisayarı kapandı; Veli açık sevkiyatlardan devam ediyor
        var open = (await veli.GetFromJsonAsync<List<ShipmentSummary>>("/" + ShipmentPaths.Shipments, ServerApiContract.Json))!;
        var resumed = await Get(veli, open.Single().ShipmentId);
        Assert.Equal(["K1-000001"], resumed.Items.Select(i => i.CutId));
        var r = await Scan(veli, s.ShipmentId, "K1000002");

        var d = await Get(veli, s.ShipmentId);
        Assert.Equal(["Veli", "Ali"], d.Items.Select(i => i.AddedBy));
        await Ok<ShipmentDetail>(await Confirm(veli, s.ShipmentId, r.ShipmentVersion));
    }

    // ---------- Çıkarma ----------

    [Fact]
    public async Task Removing_a_scanned_roll_returns_it_to_stock()
    {
        var (_, ali, _) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);
        await Scan(ali, s.ShipmentId, "K1000001");

        var removed = await Remove(ali, s.ShipmentId, "K1000001");
        Assert.Equal((true, ScanCodes.Removed, 0L), (removed.Accepted, removed.Code, removed.Stats.Count));
        Assert.Equal("in_stock", State("K1-000001"));

        var notThere = await Remove(ali, s.ShipmentId, "K1000002");
        Assert.Equal((false, ScanCodes.NotInThisShipment), (notThere.Accepted, notThere.Code));

        var back = await Scan(ali, s.ShipmentId, "K1000001");   // tekrar eklenebilir
        Assert.True(back.Accepted);
        Assert.Equal(0, back.Stats.Rejected);   // çıkarma okutmaları ret sayılmaz
    }

    // ---------- Kalite kontrol değişiklikleri sevkiyattaki topa gelirse ----------

    [Fact]
    public async Task Qc_void_of_reserved_roll_removes_it_from_shipment()
    {
        var (_, ali, _) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);
        var before = await Scan(ali, s.ShipmentId, "K1000002");

        await _server.PostOk(Batch(Pc, "K1", Voided(Cut("K1", 2, lot: "LOT-3", cm: 200))));

        Assert.Equal("voided", State("K1-000002"));
        var d = await Get(ali, s.ShipmentId);
        Assert.Empty(d.Items);
        Assert.True(d.Version > before.ShipmentVersion);   // onay bekleyen liste değişti → eski sürümle onay reddedilir
        Assert.Equal("VERSION_MISMATCH", (await Err(await Confirm(ali, s.ShipmentId, before.ShipmentVersion), 409)).Code);
    }

    [Fact]
    public async Task Duplicate_label_arriving_for_reserved_roll_quarantines_and_removes_it()
    {
        var (_, ali, _) = await People();
        var (lot3, _) = await Stock();
        var s = await OpenShipment(ali, lot3);
        await Scan(ali, s.ShipmentId, "K1000002");

        await _server.PostOk(Batch(Pc, "K1", Cut("K1", 2, lot: "LOT-3", cm: 999, created: "2026-11-15T11:00:00.000Z")));

        Assert.Equal("quarantined", State("K1-000002"));
        Assert.Empty((await Get(ali, s.ShipmentId)).Items);
        Assert.Equal(ScanCodes.Quarantined, (await Scan(ali, s.ShipmentId, "K1000002")).Code);
    }

    [Fact]
    public async Task Lot_of_reserved_roll_cannot_be_remapped()
    {
        var (_, ali, _) = await People();
        var (_, lot2) = await Stock();
        var unknownLot = await Lot("BİLİNMEYEN FİRMA X");
        await Ok<LotAliasResponse>(await Post(_anon, $"{DepotPaths.Lots}/{unknownLot}/aliases", new LotAliasRequest("bilinmeyen firma")));
        var s = await OpenShipment(ali, unknownLot);
        Assert.True((await Scan(ali, s.ShipmentId, "K1000008")).Accepted);

        var e = await Err(await Post(_anon, $"{DepotPaths.Lots}/{lot2}/aliases", new LotAliasRequest("bilinmeyen firma")), 409);
        Assert.Contains("sevkiyatta", e.Message);
    }

    // ---------- Senaryo 12: bütün işlemlerden sonra sayılar ----------

    [Fact]
    public async Task S12_stock_counts_and_lengths_stay_correct_after_everything()
    {
        var (_, ali, veli) = await People();
        var (lot3, lot2) = await Stock();
        var a = await OpenShipment(ali, lot3);
        await Scan(ali, a.ShipmentId, "K1000001");
        await Scan(ali, a.ShipmentId, "K1000002");
        await Scan(ali, a.ShipmentId, "K1000006");                    // yanlış lot
        await Remove(ali, a.ShipmentId, "K1000002");
        var last = await Scan(ali, a.ShipmentId, "K1000003");
        await Ok<ShipmentDetail>(await Confirm(ali, a.ShipmentId, last.ShipmentVersion));   // 1 + 3 sevk

        var b = await OpenShipment(veli, lot2);
        await Scan(veli, b.ShipmentId, "K1000006");
        await Ok<ShipmentDetail>(await Cancel(veli, b.ShipmentId));     // 6 stoğa döner

        var stock = (await _anon.GetFromJsonAsync<StockResponse>("/" + DepotPaths.Stock, ServerApiContract.Json))!;
        // Stokta: 2, 4, 5 (LOT-3) + 6, 7 (LOT-2) + 8 (eşleşmemiş) = 6 top; 200+400+500+600+700+800 cm
        Assert.Equal((6L, 3200L), (stock.InStock.Count, stock.InStock.LengthCm));
        var shipped = (await _anon.GetFromJsonAsync<StockResponse>("/" + DepotPaths.Stock + "?state=shipped", ServerApiContract.Json))!;
        Assert.Equal((2L, 400L), (shipped.Filtered.Count, shipped.Filtered.LengthCm));
        var lots = (await _anon.GetFromJsonAsync<List<LotInfo>>("/" + DepotPaths.Lots, ServerApiContract.Json))!;
        Assert.Equal((3L, 1100L), (lots.Single(l => l.LotId == lot3).InStockCount, lots.Single(l => l.LotId == lot3).InStockCm));
        Assert.Equal(2, lots.Single(l => l.LotId == lot2).InStockCount);

        var history = (await _anon.GetFromJsonAsync<RollDetail>("/" + DepotPaths.Rolls + "/K1000002", ServerApiContract.Json))!;
        Assert.Equal(["qc_created", "reserved", "unreserved"], history.Movements.Select(m => m.Kind));
        var closed = (await ali.GetFromJsonAsync<List<ShipmentSummary>>("/" + ShipmentPaths.Shipments + "?status=closed", ServerApiContract.Json))!;
        Assert.Equal([ShipmentStatuses.Cancelled, ShipmentStatuses.Confirmed], closed.Select(x => x.Status));
    }

    // ---------- Veritabanı korumaları ----------

    [Fact]
    public async Task Database_blocks_double_reservation_and_history_changes()
    {
        var (_, ali, veli) = await People();
        var (lot3, _) = await Stock();
        var a = await OpenShipment(ali, lot3);
        var b = await OpenShipment(veli, lot3);
        await Scan(ali, a.ShipmentId, "K1000001");
        using var db = _server.Open();

        // Uygulamada hata olsa bile aynı top ikinci aktif kaleme yazılamaz
        var ex = Assert.Throws<SqliteException>(() => db.Execute(
            "INSERT INTO shipment_items (shipment_id, cut_id, status, added_at, added_by, scan_event_id) VALUES (@b, 'K1-000001', 'active', 'x', 2, 1)",
            new { b = b.ShipmentId }));
        Assert.Contains("UNIQUE", ex.Message);
        Assert.Throws<SqliteException>(() => db.Execute("DELETE FROM shipments"));
        Assert.Throws<SqliteException>(() => db.Execute("DELETE FROM shipment_items"));
        Assert.Throws<SqliteException>(() => db.Execute("DELETE FROM scan_events"));
        Assert.Throws<SqliteException>(() => db.Execute("UPDATE scan_events SET result_code = 'OK'"));
        Assert.Throws<SqliteException>(() => db.Execute("DELETE FROM users"));
        db.Execute("UPDATE shipments SET status = 'cancelled', closed_at = 'x', closed_by = 2 WHERE shipment_id = @b", new { b = b.ShipmentId });
        Assert.Throws<SqliteException>(() => db.Execute("UPDATE shipments SET status = 'open', closed_at = NULL, closed_by = NULL WHERE shipment_id = @b", new { b = b.ShipmentId }));
    }

    [Fact]
    public async Task Confirmed_shipment_scan_and_unknown_shipment()
    {
        var (_, ali, _) = await People();
        var (lot3, _) = await Stock();
        Assert.Equal(404, (int)(await Post(ali, $"{ShipmentPaths.Shipments}/999/scans", new ScanRequest("x1", "K1000001"))).StatusCode);
        Assert.Equal(400, (int)(await Post(ali, $"{ShipmentPaths.Shipments}/1/scans", new ScanRequest("", "K1000001"))).StatusCode);
        var s = await OpenShipment(ali, lot3);
        var r = await Scan(ali, s.ShipmentId, "K1000001");
        await Ok<ShipmentDetail>(await Confirm(ali, s.ShipmentId, r.ShipmentVersion));
        Assert.Equal(ScanCodes.ShipmentNotOpen, (await Scan(ali, s.ShipmentId, "K1000002")).Code);
        Assert.Equal(ScanCodes.ShipmentNotOpen, (await Remove(ali, s.ShipmentId, "K1000001")).Code);
        Assert.Equal("shipped", State("K1-000001"));
    }
}
