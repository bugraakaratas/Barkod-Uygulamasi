using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace KumasBarkod.Server.Tests;

/// <summary>
/// Code.gs v8'in sunucunun kullandığı kısmı: anahtar kontrolü, Kesimler'e cut_id ile yazma, Stok/Sevkiyatlar'ı baştan yazma.
/// <see cref="Offline"/> = internet yok, <see cref="Version"/> = dağıtılmış script sürümü.
/// </summary>
public sealed class FakeAppsScript : HttpMessageHandler
{
    public const string Url = "https://script.google.com/macros/s/TEST/exec";
    public const string Key = "gizli";

    public int Version { get; set; } = 8;
    public bool Offline { get; set; }
    public int Posts { get; private set; }
    public Dictionary<string, JsonObject> Cuts { get; } = [];
    public Dictionary<string, (List<string> Headers, List<JsonArray> Rows, string Updated)> Snapshots { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline) throw new HttpRequestException("internet yok");
        Posts++;
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        object reply;
        if ((string?)body["key"] != Key) reply = new { ok = false, error = "Yetkisiz (anahtar yanlış)." };
        else if ((string?)body["action"] == "snapshot")
        {
            var sheet = (string)body["sheet"]!;
            Snapshots[sheet] = (body["headers"]!.AsArray().Select(h => (string)h!).ToList(),
                body["rows"]!.AsArray().Select(x => x!.AsArray()).ToList(), (string)body["updated"]!);
            reply = new { ok = true, sheet, version = Version };
        }
        else
        {
            foreach (var row in body["rows"]!.AsArray()) Cuts[(string)row!["cut_id"]!] = row.AsObject();
            reply = new { ok = true, version = Version, conflicts = Array.Empty<object>(), max_sequences = new Dictionary<string, long>() };
        }
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(reply) };
    }
}
