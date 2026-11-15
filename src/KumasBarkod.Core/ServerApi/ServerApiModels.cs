using System.Text.Json;
using KumasBarkod.Core.Sync;

namespace KumasBarkod.Core.ServerApi;

/// <summary>
/// İstasyon (QC) ↔ depo sunucusu sözleşmesi. Hem WPF uygulaması hem sunucu bu tipleri kullanır:
/// alan adı farkı yüzünden sessiz veri kaybı olmasın. JSON: camelCase (web varsayılanı).
/// Yeni alan eklemek geriye uyumludur; alan silmek/yeniden adlandırmak <see cref="ApiVersion"/> artırır.
/// </summary>
public static class ServerApiContract
{
    /// <summary>Sözleşme sürümü. İstasyon, sunucu bundan eskiyse gönderim yapmaz.</summary>
    public const int ApiVersion = 1;

    /// <summary>Varsayılan sunucu kapısı (port).</summary>
    public const int DefaultPort = 5080;

    public const string CutsBatchPath = "api/v1/cuts/batch";
    public const string HealthPath = "health";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>Hata kodları (makine okur; mesaj Türkçe ve sunucudan gelir).</summary>
public static class ServerErrorCodes
{
    /// <summary>İstasyon kodu sunucuda başka bir bilgisayara kayıtlı.</summary>
    public const string StationOwnedByOtherDevice = "STATION_OWNED_BY_OTHER_DEVICE";
    /// <summary>İstek geçersiz (eksik alan, bozuk satır). Uygulamada hata var demektir.</summary>
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string Internal = "INTERNAL";
}

/// <summary>
/// Kesim paketi. Boş <see cref="Rows"/>: el sıkışma — istasyon kaydı + sayaç öğrenme, veri yazılmaz.
/// </summary>
/// <param name="InstallId">Bilgisayarın kalıcı kimliği (Windows MachineGuid). Veritabanı kopyalansa da değişmez.</param>
/// <param name="StationCode">Bu bilgisayarın ŞU ANKİ istasyon kodu (kayıt için).</param>
public sealed record CutsBatchRequest(
    string InstallId,
    string StationCode,
    string? AppVersion,
    IReadOnlyList<CutSyncRow> Rows);

/// <summary>Sunucuda aynı ID'yi taşıyan BAŞKA bir kesim var (kayıt zamanı farklı): iki fiziksel top aynı etiketi taşıyor.</summary>
public sealed record ServerCutConflict(string CutId, string CreatedAt, string ExistingCreatedAt);

/// <param name="Accepted">Bu pakette sunucuda yeni eklenen ya da güncellenen (veya zaten aynı olan) kesim sayısı.</param>
/// <param name="MaxSequences">Sunucunun bildiği istasyon başına en büyük sıra numarası.</param>
public sealed record CutsBatchResponse(
    bool Ok,
    int ApiVersion,
    int Accepted,
    IReadOnlyList<ServerCutConflict> Conflicts,
    IReadOnlyDictionary<string, long> MaxSequences);

/// <summary>Hata yanıtı (4xx/5xx).</summary>
public sealed record ServerError(bool Ok, string Code, string Message);

public sealed record HealthResponse(bool Ok, string Service, int ApiVersion, string Version, int SchemaVersion, string ServerTime);
