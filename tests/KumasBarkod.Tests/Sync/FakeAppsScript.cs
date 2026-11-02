using System.Net;
using System.Text;
using System.Text.Json;

namespace KumasBarkod.Tests.Sync;

/// <summary>
/// Apps Script web uygulamasının (tools/sheets/Code.gs) davranışını taklit eden HTTP işleyici:
/// anahtar kontrolü, cut_id'ye göre upsert, v3 çakışma reddi (aynı ID + farklı created_at),
/// created_at sırası, max_sequences. Kesinti ve hata senaryoları için düğmeler.
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

    /// <summary>Script sürümü (gönderim yanıtında). 3'ten küçükse eski script gibi davranır: çakışmayı ezer.</summary>
    public int Version { get; set; } = 3;

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
        var conflicts = new List<object>();
        var upserted = 0;
        var appended = false;
        foreach (var row in rows)
        {
            var values = row.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            var id = values["cut_id"].GetString();
            var existing = Sheet.FindIndex(r => r["cut_id"].GetString() == id);
            if (existing >= 0)
            {
                var oldCreated = Created(Sheet[existing]);
                if (Version >= 3 && oldCreated != Created(values))
                {
                    conflicts.Add(new { cut_id = id, created_at = Created(values), existing_created_at = oldCreated });
                    continue;
                }
                Sheet[existing] = values;
            }
            else
            {
                Sheet.Add(values);
                appended = true;
            }
            upserted++;
        }

        if (Version >= 3 && appended)   // kayıt zamanına göre, eşitse ID'ye göre (Code.gs ile aynı)
        {
            var sorted = Sheet.OrderBy(Created, StringComparer.Ordinal)
                .ThenBy(r => r["cut_id"].GetString(), StringComparer.Ordinal).ToList();
            Sheet.Clear();
            Sheet.AddRange(sorted);
        }

        var maxSequences = Sheet
            .Select(r => r["cut_id"].GetString()!)
            .GroupBy(id => id[..2])
            .ToDictionary(g => g.Key, g => g.Max(id => long.Parse(id[3..])));

        if (LoseNextResponse > 0)
        {
            LoseNextResponse--;
            throw new HttpRequestException("Yanıt kayboldu (sahte).");
        }

        if (Version < 3)
            return Reply(new { ok = true, upserted = rows.Count, version = Version });
        return Reply(new { ok = true, upserted, conflicts, max_sequences = maxSequences, version = Version });
    }

    private static string? Created(Dictionary<string, JsonElement> row) =>
        row.TryGetValue("created_at", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Tabloya başka bir veritabanından gelmiş gibi satır ekler (gönderim yolu dışında).</summary>
    public void AddForeignRow(string cutId, string createdAt, string lot = "BASKA")
    {
        Sheet.Add(new Dictionary<string, JsonElement>
        {
            ["cut_id"] = JsonSerializer.SerializeToElement(cutId),
            ["station_code"] = JsonSerializer.SerializeToElement(cutId[..2]),
            ["sequence_no"] = JsonSerializer.SerializeToElement(long.Parse(cutId[3..])),
            ["lot_code"] = JsonSerializer.SerializeToElement(lot),
            ["length_cm"] = JsonSerializer.SerializeToElement(1000L),
            ["operator"] = JsonSerializer.SerializeToElement<string?>(null),
            ["status"] = JsonSerializer.SerializeToElement("active"),
            ["void_reason"] = JsonSerializer.SerializeToElement<string?>(null),
            ["voided_at"] = JsonSerializer.SerializeToElement<string?>(null),
            ["created_at"] = JsonSerializer.SerializeToElement(createdAt),
            ["print_count"] = JsonSerializer.SerializeToElement(0L),
            ["last_printed_at"] = JsonSerializer.SerializeToElement<string?>(null),
        });
    }

    private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };
}
