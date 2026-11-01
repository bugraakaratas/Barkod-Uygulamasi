using System.Net;
using System.Text;
using System.Text.Json;

namespace KumasBarkod.Tests.Sync;

/// <summary>
/// Apps Script web uygulamasının (tools/sheets/Code.gs) davranışını taklit eden HTTP işleyici:
/// anahtar kontrolü, cut_id'ye göre upsert, tablo sırası. Kesinti ve hata senaryoları için düğmeler.
/// </summary>
public sealed class FakeAppsScript : HttpMessageHandler
{
    public const string Key = "gizli-anahtar";

    /// <summary>Tablo: cut_id → satır (sıra eklenme sırası).</summary>
    public List<Dictionary<string, JsonElement>> Sheet { get; } = [];

    public int RequestCount { get; private set; }
    public List<int> BatchSizes { get; } = [];
    public string? LastBody { get; private set; }

    /// <summary>Sonraki N istekte ağ hatası (internet yok).</summary>
    public int FailNextAsOffline { get; set; }

    /// <summary>Sonraki N istekte tabloya YAZ ama yanıtı kaybet (bağlantı yazımdan sonra koptu).</summary>
    public int LoseNextResponse { get; set; }

    /// <summary>Eski (v1) script gibi davran: okuma eylemini tanıma.</summary>
    public bool ExportUnsupported { get; set; }

    public int ExportRequests { get; private set; }

    /// <summary>Tablodaki bir hücreyi elle değiştir (bozulmuş tablo senaryosu).</summary>
    public void SetCell(string cutId, string column, object? value) =>
        Row(cutId)[column] = JsonSerializer.SerializeToElement(value);

    public HttpStatusCode? ForceStatus { get; set; }
    public string? ForceBody { get; set; }

    /// <summary>İstek işlenirken çalışır (ör. gönderim sırasında yeni değişiklik oluştur).</summary>
    public Action? DuringRequest { get; set; }

    public int RowCount(string cutId) => Sheet.Count(r => r["cut_id"].GetString() == cutId);

    public Dictionary<string, JsonElement> Row(string cutId) => Sheet.Single(r => r["cut_id"].GetString() == cutId);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        DuringRequest?.Invoke();

        if (FailNextAsOffline > 0)
        {
            FailNextAsOffline--;
            throw new HttpRequestException("Bağlantı kurulamadı (sahte).");
        }

        if (ForceStatus is { } status)
            return new HttpResponseMessage(status) { Content = new StringContent(ForceBody ?? "", Encoding.UTF8) };

        using var doc = JsonDocument.Parse(LastBody);
        var root = doc.RootElement;
        if (root.GetProperty("key").GetString() != Key)
            return Reply(new { ok = false, error = "Anahtar geçersiz." });

        if (root.TryGetProperty("action", out var action) && action.GetString() == "export")
        {
            if (ExportUnsupported)   // v1 script: "action" bilinmez, normal upsert yanıtı döner
                return Reply(new { ok = true, upserted = 0, version = 1 });

            var offset = root.GetProperty("offset").GetInt32();
            var limit = root.GetProperty("limit").GetInt32();
            ExportRequests++;
            return Reply(new { ok = true, total = Sheet.Count, rows = Sheet.Skip(offset).Take(limit).ToList(), version = 2 });
        }

        var rows = root.GetProperty("rows").EnumerateArray().ToList();
        BatchSizes.Add(rows.Count);
        foreach (var row in rows)
        {
            var values = row.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var id = values["cut_id"].GetString();
            var existing = Sheet.FindIndex(r => r["cut_id"].GetString() == id);
            if (existing >= 0) Sheet[existing] = values;
            else Sheet.Add(values);
        }

        if (LoseNextResponse > 0)
        {
            LoseNextResponse--;
            throw new HttpRequestException("Yanıt kayboldu (sahte).");
        }

        return Reply(new { ok = true, upserted = rows.Count, version = 1 });
    }

    private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}
