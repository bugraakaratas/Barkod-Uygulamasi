using System.Net.Http.Json;
using Dapper;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Server.Sheets;
using Microsoft.Extensions.DependencyInjection;
using static KumasBarkod.Server.Tests.Rows;

namespace KumasBarkod.Server.Tests;

/// <summary>Faz 6: Google Sheets kopyası depo sunucusundan (Kesimler + Stok + Sevkiyatlar).</summary>
public sealed partial class ShipmentTests
{
    private SheetsSyncService SheetsSvc => _server.Services.GetRequiredService<SheetsSyncService>();

    private Task<HttpResponseMessage> PutSheets(HttpClient c, string? url, string? key) =>
        c.PutAsJsonAsync("/" + SheetsPaths.Settings, new SheetsSettingsRequest(url, key), ServerApiContract.Json);

    private async Task<SheetsStatusResponse> SheetsStatus(HttpClient c) =>
        await Ok<SheetsStatusResponse>(await c.GetAsync("/" + SheetsPaths.Settings));

    [Fact]
    public async Task Sheets_ayari_yalnizca_yetkili_gorur_ve_degistirir()
    {
        var (_, ali, _) = await People();
        Assert.Equal(401, (int)(await _anon.GetAsync("/" + SheetsPaths.Settings)).StatusCode);
        Assert.Equal(403, (int)(await ali.GetAsync("/" + SheetsPaths.Settings)).StatusCode);
        await Err(await PutSheets(ali, FakeAppsScript.Url, FakeAppsScript.Key), 403);
        Assert.Equal(0, _server.Sheets.Posts);
    }

    [Fact]
    public async Task Sheets_yanlis_ayar_kaydedilmez_eski_script_internetsizlik_yanlis_anahtar()
    {
        var (boss, _, _) = await People();
        await Err(await PutSheets(boss, "http://script.google.com/x", "gizli"), 400);   // https değil
        await Err(await PutSheets(boss, FakeAppsScript.Url, ""), 400);                  // anahtar yok
        Assert.Contains("Yetkisiz", (await Err(await PutSheets(boss, FakeAppsScript.Url, "yanlis"), 400)).Message);

        _server.Sheets.Version = 7;
        var old = await Err(await PutSheets(boss, FakeAppsScript.Url, FakeAppsScript.Key), 400);
        Assert.Equal(SheetsSyncService.SheetsErrorCode, old.Code);
        Assert.Contains("v7", old.Message);

        _server.Sheets.Version = 8;
        _server.Sheets.Offline = true;
        Assert.Contains("ulaşılamıyor", (await Err(await PutSheets(boss, FakeAppsScript.Url, FakeAppsScript.Key), 400)).Message);

        Assert.False((await SheetsStatus(boss)).Configured);
    }

    [Fact]
    public async Task Sheets_butun_toplar_Kesimlere_stok_ve_sevkiyatlar_sayfalarina_gider()
    {
        var (boss, ali, _) = await People();
        var (lot3, _) = await Stock();

        // Ayarlanmadan önce hiçbir şey gönderilmez, kuyruk bekler.
        Assert.True(await SheetsSvc.RunOnceAsync(default));
        Assert.Equal(0, _server.Sheets.Posts);

        await Ok<SheetsStatusResponse>(await PutSheets(boss, FakeAppsScript.Url, FakeAppsScript.Key));
        var raw = await (await boss.GetAsync("/" + SheetsPaths.Settings)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(FakeAppsScript.Key, raw);   // anahtar geri gönderilmez
        Assert.Equal(9, (await SheetsStatus(boss)).PendingCuts);

        Assert.True(await SheetsSvc.RunOnceAsync(default));
        Assert.Equal(9, _server.Sheets.Cuts.Count);
        Assert.Equal("voided", (string?)_server.Sheets.Cuts["K1-000009"]["status"]);
        Assert.Equal("bilinmeyen firma", (string?)_server.Sheets.Cuts["K1-000008"]["lot_code"]);
        var status = await SheetsStatus(boss);
        Assert.Equal(0, status.PendingCuts);
        Assert.NotNull(status.LastSnapshotAt);
        Assert.Null(status.LastError);

        var stock = _server.Sheets.Snapshots["Stok"];
        Assert.Equal("Barkod", stock.Headers[0]);
        Assert.Equal(8, stock.Rows.Count);   // iptal edilen K1-9 stokta değil
        var k8 = stock.Rows.Single(r => (string?)r[0] == "K1-000008");
        Assert.Equal("lot tanımsız", (string?)k8[7]);
        Assert.Equal(1.0, (double)stock.Rows.Single(r => (string?)r[0] == "K1-000001")[2]!);   // metre sayı
        Assert.Empty(_server.Sheets.Snapshots["Sevkiyatlar"].Rows);

        // Sevkiyat: okutulan top "Sevkiyatta", onaydan sonra stoktan çıkar; Sevkiyatlar'a satır gelir.
        var s = await OpenShipment(ali, lot3);
        await Scan(ali, s.ShipmentId, "K1-000001");
        var last = await Scan(ali, s.ShipmentId, "K1-000002");
        Assert.True(await SheetsSvc.RunOnceAsync(default));
        Assert.Empty(_server.Sheets.Snapshots["Sevkiyatlar"].Rows);   // 2 dk dolmadı: tablo her okutmada yazılmaz

        _server.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await SheetsSvc.RunOnceAsync(default));
        var k1 = _server.Sheets.Snapshots["Stok"].Rows.Single(r => (string?)r[0] == "K1-000001");
        Assert.Equal("Sevkiyatta (onay bekliyor)", (string?)k1[3]);
        Assert.Equal(s.ShipmentNo, (string?)k1[4]);

        await Ok<ShipmentDetail>(await Confirm(ali, s.ShipmentId, last.ShipmentVersion));
        _server.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await SheetsSvc.RunOnceAsync(default));
        Assert.Equal(6, _server.Sheets.Snapshots["Stok"].Rows.Count);
        var ship = Assert.Single(_server.Sheets.Snapshots["Sevkiyatlar"].Rows);
        Assert.Equal([s.ShipmentNo, "Onaylandı (sevk edildi)", "LOT-3"], ship.Take(3).Select(x => (string?)x));
        Assert.Equal(2L, (long)ship[3]!);
        Assert.Equal(3.0, (double)ship[4]!);

        // Veri değişmediyse süre dolsa da yeniden yazılmaz.
        var posts = _server.Sheets.Posts;
        _server.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await SheetsSvc.RunOnceAsync(default));
        Assert.Equal(posts, _server.Sheets.Posts);
    }

    [Fact]
    public async Task Sheets_QC_degisikligi_kuyruga_girer_internet_yokken_bekler_gelince_gider()
    {
        var (boss, _, _) = await People();
        await Stock();
        await Ok<SheetsStatusResponse>(await PutSheets(boss, FakeAppsScript.Url, FakeAppsScript.Key));
        Assert.True(await SheetsSvc.RunOnceAsync(default));

        // QC'de yeniden basım ve iptal: tetikleyici topu kuyruğa koyar.
        await _server.PostOk(Batch(Pc, "K1", Cut("K1", 2, lot: "LOT-3", cm: 200, printCount: 3),
            Voided(Cut("K1", 3, lot: "LOT-3", cm: 300))));
        Assert.Equal(2, (await SheetsStatus(boss)).PendingCuts);

        _server.Sheets.Offline = true;
        Assert.False(await SheetsSvc.RunOnceAsync(default));
        var down = await SheetsStatus(boss);
        Assert.Equal(2, down.PendingCuts);
        Assert.Contains("ulaşılamıyor", down.LastError);

        _server.Sheets.Offline = false;
        Assert.True(await SheetsSvc.RunOnceAsync(default));
        Assert.Equal(3L, (long)_server.Sheets.Cuts["K1-000002"]["print_count"]!);
        Assert.Equal("voided", (string?)_server.Sheets.Cuts["K1-000003"]["status"]);
        var up = await SheetsStatus(boss);
        Assert.Equal(0, up.PendingCuts);
        Assert.Null(up.LastError);
    }

    [Fact]
    public async Task Sheets_gonderim_sirasinda_degisen_top_kuyrukta_kalir()
    {
        await Stock();
        using var c = _server.Open();
        var before = c.ExecuteScalar<long>("SELECT seq FROM sheets_pending WHERE cut_id = 'K1-000002'");
        await _server.PostOk(Batch(Pc, "K1", Cut("K1", 2, lot: "LOT-3", cm: 200, printCount: 4)));
        var after = c.ExecuteScalar<long>("SELECT seq FROM sheets_pending WHERE cut_id = 'K1-000002'");
        Assert.True(after > before);
        // Gönderen eski seq ile silmeye çalışırsa satır kalır.
        Assert.Equal(0, c.Execute("DELETE FROM sheets_pending WHERE cut_id = 'K1-000002' AND seq = @before", new { before }));
        Assert.Equal(9, c.ExecuteScalar<long>("SELECT count(*) FROM sheets_pending"));
    }

    [Fact]
    public async Task Sheets_anahtar_bos_birakilirsa_korunur_adres_bos_kapatir_baska_tablo_hepsini_yeniden_alir()
    {
        var (boss, _, _) = await People();
        await Stock();
        await Ok<SheetsStatusResponse>(await PutSheets(boss, FakeAppsScript.Url, FakeAppsScript.Key));
        Assert.True(await SheetsSvc.RunOnceAsync(default));
        Assert.Equal(0, (await SheetsStatus(boss)).PendingCuts);

        // Aynı adres, anahtar boş: kayıtlı anahtar korunur, kuyruk yeniden dolmaz.
        Assert.True((await Ok<SheetsStatusResponse>(await PutSheets(boss, FakeAppsScript.Url, null))).Configured);
        Assert.Equal(0, (await SheetsStatus(boss)).PendingCuts);

        // Başka tablo: bütün toplar oraya da gider.
        var other = await Ok<SheetsStatusResponse>(await PutSheets(boss, FakeAppsScript.Url + "?yeni", null));
        Assert.Equal(9, other.PendingCuts);

        var off = await Ok<SheetsStatusResponse>(await PutSheets(boss, "", null));
        Assert.False(off.Configured);
        var posts = _server.Sheets.Posts;
        Assert.True(await SheetsSvc.RunOnceAsync(default));
        Assert.Equal(posts, _server.Sheets.Posts);
    }
}
