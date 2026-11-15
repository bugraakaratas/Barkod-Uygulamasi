using System.Net.Http.Json;
using Dapper;
using KumasBarkod.Core.ServerApi;
using KumasBarkod.Server.Data;
using Microsoft.Data.Sqlite;
using static KumasBarkod.Server.Tests.Rows;

namespace KumasBarkod.Server.Tests;

/// <summary>İstasyondan gelen kesimlerin stoğa işlenmesi (Faz 1).</summary>
public sealed class CutIngestTests : IDisposable
{
    private const string Pc1 = "PC-1";
    private const string Pc2 = "PC-2";

    private readonly ServerFixture _server = new();

    public void Dispose()
    {
        // Her test veriyi tutarlı bırakmalı
        Assert.Empty(_server.Invariants());
        _server.Dispose();
    }

    private (string State, string QcStatus, long PrintCount) Roll(string cutId)
    {
        using var c = _server.Open();
        return c.QuerySingle<(string, string, long)>(
            "SELECT state, qc_status, print_count FROM rolls WHERE cut_id = @cutId", new { cutId });
    }

    private List<(string Kind, string? From, string To)> Movements(string cutId)
    {
        using var c = _server.Open();
        return c.Query<(string, string?, string)>(
            "SELECT kind, from_state, to_state FROM stock_movements WHERE cut_id = @cutId ORDER BY movement_id",
            new { cutId }).AsList();
    }

    private long Count(string sql)
    {
        using var c = _server.Open();
        return c.ExecuteScalar<long>(sql);
    }

    // ---------- Sağlık ----------

    [Fact]
    public async Task Health_reports_service_and_versions()
    {
        var health = await _server.CreateClient().GetFromJsonAsync<HealthResponse>("/health", ServerApiContract.Json);

        Assert.True(health!.Ok);
        Assert.Equal("KumasBarkod.Server", health.Service);
        Assert.Equal(ServerApiContract.ApiVersion, health.ApiVersion);
        Assert.Equal(ServerMigrations.LatestVersion, health.SchemaVersion);
        Assert.Equal("2026-11-15T09:00:00.000Z", health.ServerTime);
    }

    [Fact]
    public void Database_lives_in_test_directory()
    {
        Assert.StartsWith(_server.DataDirectory, _server.Db.DatabasePath);
    }

    // ---------- Yeni kesim ----------

    [Fact]
    public async Task New_cut_enters_stock_with_qc_created_movement()
    {
        var r = await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1), Cut("K1", 2, cm: 1800)));

        Assert.Equal(2, r.Accepted);
        Assert.Empty(r.Conflicts);
        Assert.Equal(2, r.MaxSequences["K1"]);
        Assert.Equal(("in_stock", "active", 1L), Roll("K1-000001"));
        Assert.Equal([("qc_created", null, "in_stock")], Movements("K1-000001"));

        using var c = _server.Open();
        var m = c.QuerySingle<(string Occurred, string Recorded, string Station)>(
            "SELECT occurred_at, recorded_at, station_code FROM stock_movements WHERE cut_id = 'K1-000001'");
        Assert.Equal("2026-11-15T08:00:00.000Z", m.Occurred);   // QC zamanı
        Assert.Equal("2026-11-15T09:00:00.000Z", m.Recorded);   // sunucu zamanı
        Assert.Equal("K1", m.Station);
    }

    [Fact]
    public async Task Handshake_registers_station_without_writing_rolls()
    {
        var r = await _server.PostOk(Batch(Pc1, "K1"));

        Assert.Equal(0, r.Accepted);
        Assert.Empty(r.MaxSequences);
        Assert.Equal(1, Count("SELECT count(*) FROM stations WHERE station_code = 'K1' AND install_id = 'PC-1'"));
        Assert.Equal(0, Count("SELECT count(*) FROM rolls"));
    }

    [Fact]
    public async Task Cut_that_arrives_already_voided_shows_both_steps_in_history()
    {
        await _server.PostOk(Batch(Pc1, "K1", Voided(Cut("K1", 1))));

        Assert.Equal(("voided", "voided", 1L), Roll("K1-000001"));
        Assert.Equal([("qc_created", null, "in_stock"), ("qc_voided", "in_stock", "voided")], Movements("K1-000001"));
    }

    // ---------- Tekrar gönderim (yanıt kaybolunca) ----------

    [Fact]
    public async Task Same_batch_sent_three_times_has_single_effect()
    {
        var batch = Batch(Pc1, "K1", Cut("K1", 1), Voided(Cut("K1", 2)));

        for (var i = 0; i < 3; i++)
            Assert.Empty((await _server.PostOk(batch)).Conflicts);

        Assert.Equal(2, Count("SELECT count(*) FROM rolls"));
        Assert.Equal(3, Count("SELECT count(*) FROM stock_movements"));
    }

    [Fact]
    public async Task Print_count_only_moves_forward()
    {
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, printCount: 2, lastPrinted: "2026-11-15T08:05:00.000Z")));
        // Eski bir paket geç geldi
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, printCount: 1, lastPrinted: "2026-11-15T08:00:01.000Z")));

        using var c = _server.Open();
        var (count, last) = c.QuerySingle<(long, string)>("SELECT print_count, last_printed_at FROM rolls");
        Assert.Equal(2, count);
        Assert.Equal("2026-11-15T08:05:00.000Z", last);
        Assert.Single(Movements("K1-000001"));   // baskı stok hareketi değildir
    }

    [Fact]
    public async Task Void_is_applied_once_and_never_undone()
    {
        var cut = Cut("K1", 1);
        await _server.PostOk(Batch(Pc1, "K1", cut));
        await _server.PostOk(Batch(Pc1, "K1", Voided(cut)));
        await _server.PostOk(Batch(Pc1, "K1", Voided(cut)));
        await _server.PostOk(Batch(Pc1, "K1", cut));   // geç gelen eski hâl: iptali geri almaz

        Assert.Equal(("voided", "voided", 1L), Roll("K1-000001"));
        Assert.Equal([("qc_created", null, "in_stock"), ("qc_voided", "in_stock", "voided")], Movements("K1-000001"));
        using var c = _server.Open();
        Assert.Equal("Çift kayıt", c.ExecuteScalar<string>("SELECT void_reason FROM rolls"));
    }

    // ---------- Aynı ID'li iki top ----------

    [Fact]
    public async Task Same_id_with_different_creation_time_is_quarantined_not_overwritten()
    {
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 5, lot: "LOT-3", cm: 2550)));

        // Aynı istasyon kodu, aynı bilgisayar ama başka bir veritabanından (ör. sıfırdan kurulum) farklı bir top
        var other = Cut("K1", 5, lot: "LOT-7", cm: 900, created: "2026-11-15T10:00:00.000Z");
        var r = await _server.PostOk(Batch(Pc1, "K1", other));

        var conflict = Assert.Single(r.Conflicts);
        Assert.Equal(("K1-000005", "2026-11-15T10:00:00.000Z", "2026-11-15T08:00:00.000Z"),
            (conflict.CutId, conflict.CreatedAt, conflict.ExistingCreatedAt));
        Assert.Equal(0, r.Accepted);

        using (var c = _server.Open())
        {
            // İlk top ezilmedi
            Assert.Equal(("LOT-3", 2550L), c.QuerySingle<(string, long)>("SELECT lot_code_raw, length_cm FROM rolls"));
            Assert.Equal(1L, c.ExecuteScalar<long>("SELECT count(*) FROM cut_conflicts"));
            Assert.Contains("LOT-7", c.ExecuteScalar<string>("SELECT payload_json FROM cut_conflicts"));
        }
        Assert.Equal("quarantined", Roll("K1-000005").State);
        Assert.Equal([("qc_created", null, "in_stock"), ("quarantined", "in_stock", "quarantined")], Movements("K1-000005"));

        // İstasyon kuyruğunda bekleyen çakışma tekrar tekrar gelir: yeni kayıt/hareket yok
        await _server.PostOk(Batch(Pc1, "K1", other));
        Assert.Equal(1, Count("SELECT count(*) FROM cut_conflicts"));
        Assert.Equal(2, Movements("K1-000005").Count);
    }

    [Fact]
    public async Task Same_id_and_time_but_different_length_is_a_conflict()
    {
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, cm: 2550)));

        var r = await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, cm: 2551)));

        Assert.Single(r.Conflicts);
        Assert.Equal(2550, Count("SELECT length_cm FROM rolls"));
    }

    [Fact]
    public async Task Void_of_quarantined_roll_keeps_quarantine()
    {
        var cut = Cut("K1", 1);
        await _server.PostOk(Batch(Pc1, "K1", cut));
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1, created: "2026-11-15T10:00:00.000Z")));

        await _server.PostOk(Batch(Pc1, "K1", Voided(cut)));

        Assert.Equal(("quarantined", "voided", 1L), Roll("K1-000001"));
        Assert.Equal(("qc_voided", "quarantined", "quarantined"), Movements("K1-000001")[^1]);
    }

    // ---------- İstasyon kaydı ----------

    [Fact]
    public async Task Station_code_owned_by_another_computer_is_rejected_and_nothing_written()
    {
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1)));

        var error = await _server.PostError(Batch(Pc2, "K1", Cut("K1", 2)), 409);

        Assert.False(error.Ok);
        Assert.Equal(ServerErrorCodes.StationOwnedByOtherDevice, error.Code);
        Assert.Contains("başka bir bilgisayara kayıtlı", error.Message);
        Assert.Equal(1, Count("SELECT count(*) FROM rolls"));
        Assert.Equal(1, Count("SELECT count(*) FROM stock_movements"));
    }

    [Fact]
    public async Task Computer_can_send_old_cuts_after_its_station_code_changed()
    {
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1)));

        // Aynı bilgisayar Ayarlar'dan K2 oldu; kuyruğunda hâlâ K1 kesimleri var
        var r = await _server.PostOk(Batch(Pc1, "K2", Voided(Cut("K1", 1)), Cut("K2", 1)));

        Assert.Equal(2, r.Accepted);
        Assert.Equal(2, Count("SELECT count(*) FROM stations WHERE install_id = 'PC-1'"));
    }

    [Fact]
    public async Task Old_cuts_under_a_code_owned_elsewhere_are_still_taken_into_stock()
    {
        await _server.PostOk(Batch(Pc2, "K2", Cut("K2", 1, lot: "LOT-2")));

        // PC-1 eskiden yanlışlıkla K2 kullanmıştı, şimdi K1. Eski K2 kesimleri gerçek toplar: sisteme girmeli.
        var r = await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1), Cut("K2", 9), Cut("K2", 1, lot: "LOT-9", created: "2026-11-15T07:00:00.000Z")));

        Assert.Equal(2, r.Accepted);
        Assert.Equal("K2-000001", Assert.Single(r.Conflicts).CutId);   // aynı numara iki topta → karantina
        Assert.Equal("quarantined", Roll("K2-000001").State);
        Assert.Equal("in_stock", Roll("K2-000009").State);
        Assert.Equal(9, r.MaxSequences["K2"]);
        using var c = _server.Open();
        Assert.Equal("PC-2", c.ExecuteScalar<string>("SELECT install_id FROM stations WHERE station_code = 'K2'"));  // sahiplik değişmedi
    }

    [Fact]
    public async Task Rejected_current_code_rolls_back_old_code_registration()
    {
        await _server.PostOk(Batch(Pc2, "K1"));

        await _server.PostError(Batch(Pc1, "K1", Cut("K3", 1)), 409);

        Assert.Equal(0, Count("SELECT count(*) FROM rolls"));
        Assert.Equal(0, Count("SELECT count(*) FROM stations WHERE station_code = 'K3'"));
    }

    // ---------- Geçersiz istek ----------

    [Theory]
    [InlineData("id")]
    [InlineData("lot")]
    [InlineData("length")]
    [InlineData("time")]
    [InlineData("void")]
    [InlineData("longlot")]
    public async Task Invalid_row_rejects_whole_batch(string defect)
    {
        var good = Cut("K1", 1);
        var bad = defect switch
        {
            "id" => Cut("K1", 2) with { CutId = "K1-000003" },
            "lot" => Cut("K1", 2, lot: " LOT"),
            "length" => Cut("K1", 2, cm: 0),
            "time" => Cut("K1", 2, created: "15.11.2026 08:00"),
            "void" => Cut("K1", 2) with { Status = "voided" },
            _ => Cut("K1", 2, lot: new string('L', 51)),
        };

        var error = await _server.PostError(Batch(Pc1, "K1", good, bad), 400);

        Assert.Equal(ServerErrorCodes.InvalidRequest, error.Code);
        Assert.Contains("Paket yazılmadı", error.Message);
        Assert.Equal(0, Count("SELECT count(*) FROM rolls"));
        Assert.Equal(0, Count("SELECT count(*) FROM stations"));
    }

    [Fact]
    public async Task Duplicate_cut_in_one_batch_is_rejected()
    {
        var error = await _server.PostError(Batch(Pc1, "K1", Cut("K1", 1), Voided(Cut("K1", 1))), 400);
        Assert.Contains("iki kez", error.Message);
    }

    [Fact]
    public async Task Bad_json_and_missing_fields_are_400_not_500()
    {
        var client = _server.CreateClient();
        using var broken = await client.PostAsync("/" + ServerApiContract.CutsBatchPath,
            new StringContent("{ bozuk", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(400, (int)broken.StatusCode);

        await _server.PostError(new CutsBatchRequest("", "K1", null, []), 400);
        await _server.PostError(new CutsBatchRequest(Pc1, "k1", null, []), 400);
    }

    [Fact]
    public async Task Too_large_batch_is_rejected()
    {
        var rows = Enumerable.Range(1, 501).Select(i => Cut("K1", i)).ToArray();
        await _server.PostError(Batch(Pc1, "K1", rows), 400);
    }

    // ---------- Eş zamanlılık ----------

    [Fact]
    public async Task Twenty_parallel_copies_of_same_batch_create_one_roll()
    {
        var batch = Batch(Pc1, "K1", Cut("K1", 1), Cut("K1", 2));
        var client = _server.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _server.PostBatch(batch, client)));

        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode));
        Assert.Equal(2, Count("SELECT count(*) FROM rolls"));
        Assert.Equal(2, Count("SELECT count(*) FROM stock_movements"));
    }

    [Fact]
    public async Task Two_computers_racing_for_same_station_code_one_wins()
    {
        var client = _server.CreateClient();
        var tasks = Enumerable.Range(0, 10)
            .Select(i => _server.PostBatch(Batch(i % 2 == 0 ? Pc1 : Pc2, "K1", Cut("K1", i + 1)), client))
            .ToList();
        var responses = await Task.WhenAll(tasks);

        var owner = (await Task.WhenAll(responses.Select(async r => (r, body: await r.Content.ReadAsStringAsync()))))
            .ToList();
        var ok = owner.Count(x => x.r.IsSuccessStatusCode);
        var conflict = owner.Count(x => (int)x.r.StatusCode == 409);
        Assert.Equal(10, ok + conflict);
        Assert.Equal(5, ok);   // tek bir bilgisayarın 5 isteği
        using var c = _server.Open();
        var installId = c.ExecuteScalar<string>("SELECT install_id FROM stations WHERE station_code = 'K1'");
        var parity = installId == Pc1 ? 0 : 1;
        Assert.All(c.Query<long>("SELECT sequence_no FROM rolls"), seq => Assert.Equal(parity, (seq - 1) % 2));
    }

    // ---------- Veritabanı korumaları (uygulamada hata olsa bile) ----------

    [Fact]
    public async Task Database_blocks_history_changes_and_illegal_transitions()
    {
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1), Voided(Cut("K1", 2))));
        using var c = _server.Open();

        void Blocked(string sql, string reason)
        {
            var ex = Assert.Throws<SqliteException>(() => c.Execute(sql));
            Assert.Contains(reason, ex.Message);
        }

        Blocked("UPDATE stock_movements SET note = 'x'", "değiştirilemez");
        Blocked("DELETE FROM stock_movements", "silinemez");
        Blocked("DELETE FROM rolls WHERE cut_id = 'K1-000001'", "silinemez");
        Blocked("UPDATE rolls SET length_cm = 1 WHERE cut_id = 'K1-000001'", "QC alanları");
        Blocked("UPDATE rolls SET lot_code_raw = 'LOT-9' WHERE cut_id = 'K1-000001'", "QC alanları");
        Blocked("UPDATE rolls SET qc_status = 'active', void_reason = NULL, voided_at = NULL WHERE cut_id = 'K1-000002'", "geri alınamaz");
        Blocked("UPDATE rolls SET state = 'shipped' WHERE cut_id = 'K1-000001'", "durum geçişi");      // stok → sevk (okutmadan) yok
        Blocked("UPDATE rolls SET state = 'in_stock' WHERE cut_id = 'K1-000002'", "durum geçişi");     // iptal → stok yok
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE rolls SET state = 'kayip' WHERE cut_id = 'K1-000001'"));
        Assert.Throws<SqliteException>(() => c.Execute("UPDATE rolls SET state = 'voided' WHERE cut_id = 'K1-000001'")); // QC iptali olmadan
        Assert.Throws<SqliteException>(() => c.Execute(
            "INSERT INTO stock_movements (cut_id, kind, from_state, to_state, occurred_at, recorded_at) VALUES ('K1-000001', 'qc_created', 'in_stock', 'in_stock', 'x', 'x')"));
        Assert.Throws<SqliteException>(() => c.Execute(
            "INSERT INTO stock_movements (cut_id, kind, to_state, occurred_at, recorded_at) VALUES ('K9-000001', 'qc_created', 'in_stock', 'x', 'x')"));
    }

    [Fact]
    public void Invariant_check_detects_state_without_movement()
    {
        using (var c = _server.Open())
        {
            c.Execute("""
                INSERT INTO rolls (cut_id, station_code, sequence_no, lot_code_raw, length_cm, qc_status, qc_created_at,
                                   state, first_seen_at, updated_at)
                VALUES ('K1-000001', 'K1', 1, 'LOT', 100, 'active', 'x', 'in_stock', 'x', 'x')
                """);
        }

        var problems = _server.Invariants();
        Assert.Contains(problems, p => p.Contains("son hareket YOK"));
        Assert.Contains(problems, p => p.Contains("qc_created hareketi 0 adet"));

        // Dispose'daki kontrol geçsin diye düzelt
        using (var c = _server.Open())
            c.Execute("INSERT INTO stock_movements (cut_id, kind, to_state, occurred_at, recorded_at) VALUES ('K1-000001', 'qc_created', 'in_stock', 'x', 'x')");
    }

    // ---------- Yönetim komutu ----------

    [Fact]
    public async Task Releasing_station_lets_replacement_computer_register()
    {
        await _server.PostOk(Batch(Pc1, "K1", Cut("K1", 1)));
        await _server.PostError(Batch(Pc2, "K1", Cut("K1", 2)), 409);

        Assert.Equal(0, AdminCommands.ReleaseStation(new ServerPaths(_server.DataDirectory), "K1"));

        var r = await _server.PostOk(Batch(Pc2, "K1", Cut("K1", 2)));
        Assert.Equal(1, r.Accepted);
        Assert.Equal(2, r.MaxSequences["K1"]);
        Assert.Equal(1, AdminCommands.ReleaseStation(new ServerPaths(_server.DataDirectory), "K7"));   // kayıtlı değil
    }
}
