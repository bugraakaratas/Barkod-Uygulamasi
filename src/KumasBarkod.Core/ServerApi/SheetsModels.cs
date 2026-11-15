namespace KumasBarkod.Core.ServerApi;

/// <summary>Depo sunucusunun Google Sheets kopyası (Faz 6): ayar ve durum uçları (yalnızca yetkili).</summary>
public static class SheetsPaths
{
    public const string Settings = "api/v1/settings/sheets";
}

/// <summary>Adres boş = Sheets kopyası kapalı. Anahtar boş bırakılırsa kayıtlı anahtar korunur.</summary>
public sealed record SheetsSettingsRequest(string? Url, string? Key);

/// <summary>
/// Sheets kopyasının durumu. Anahtar hiçbir zaman geri gönderilmez (yalnızca kayıtlı olup olmadığı).
/// Zamanlar UTC (ISO 8601).
/// </summary>
public sealed record SheetsStatusResponse(
    bool Configured,
    string? Url,
    bool KeySet,
    long PendingCuts,
    string? LastSuccessAt,
    string? LastSnapshotAt,
    string? LastError,
    string? LastErrorAt);
