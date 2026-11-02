using KumasBarkod.Core.Domain;

namespace KumasBarkod.Core.Sync;

/// <summary>Sheets'e gönderilen bir satır: <c>cuts</c> tablosunun tüm alanları (geri kurma için).</summary>
public sealed record CutSyncRow(
    string CutId,
    string StationCode,
    long SequenceNo,
    string LotCode,
    long LengthCm,
    string? Operator,
    string Status,
    string? VoidReason,
    string? VoidedAt,
    string CreatedAt,
    long PrintCount,
    string? LastPrintedAt)
{
    public static CutSyncRow From(Cut c) => new(
        c.CutId, c.StationCode, c.SequenceNo, c.LotCode, c.LengthCm, c.Operator, c.Status,
        c.VoidReason, c.VoidedAt, c.CreatedAt, c.PrintCount, c.LastPrintedAt);
}

/// <summary>Apps Script web adresi ve anahtarı (ayarlarda durur, depoya girmez).</summary>
public sealed record SyncTarget(string Url, string Key);

public enum SyncFailureKind
{
    /// <summary>İnternet yok, Google'a ulaşılamıyor, zaman aşımı. Kendiliğinden düzelir.</summary>
    Offline,
    /// <summary>Script hata döndü (yanlış anahtar, tablo meşgul, geçersiz veri) ya da yanıt anlaşılamadı.</summary>
    Rejected,
}

public sealed class SyncException(SyncFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public SyncFailureKind Kind { get; } = kind;
}

/// <summary>Satırları uzak tabloya gönderir. Başarısızlıkta <see cref="SyncException"/> atar.</summary>
public interface ISyncTransport
{
    /// <summary>
    /// Boş liste: yalnızca bağlantı ve anahtar testi. Tabloda aynı ID'yi taşıyan BAŞKA bir kesim
    /// varsa o satır yazılmaz, <see cref="SendResult.Conflicts"/> içinde döner.
    /// </summary>
    Task<SendResult> SendAsync(SyncTarget target, IReadOnlyList<CutSyncRow> rows, CancellationToken cancellationToken);

    /// <summary>Geri kurma: tablodaki satırları [offset, offset+limit) aralığında okur.</summary>
    Task<ExportPage> ExportAsync(SyncTarget target, int offset, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// Tabloda aynı ID ile başka bir kesim var (kayıt zamanı farklı): başka bir veritabanı aynı
/// istasyon koduyla aynı numarayı üretmiş. Tablodaki eski satırın üzerine yazılmadı.
/// </summary>
public sealed record SyncConflict(string CutId, string CreatedAt, string ExistingCreatedAt);

/// <summary>
/// Gönderim sonucu. <see cref="MaxSequences"/>: tablodaki istasyon başına en büyük sıra numarası;
/// yerel sayaç bunun altında kalmamalı.
/// </summary>
public sealed record SendResult(IReadOnlyList<SyncConflict> Conflicts, IReadOnlyDictionary<string, long> MaxSequences);

/// <summary>Tablodan okunan bir sayfa; <see cref="Total"/> tablodaki toplam veri satırı.</summary>
public sealed record ExportPage(int Total, IReadOnlyList<CutSyncRow> Rows);

public enum SyncState
{
    NotConfigured,
    Idle,
    Sending,
    Offline,
    Error,
}

/// <summary>Durum çubuğunda gösterilen gönderim durumu.</summary>
public sealed record SyncStatus(
    SyncState State,
    long PendingCount,
    DateTimeOffset? LastSuccessUtc,
    string? LastError,
    DateTimeOffset? NextAttemptUtc);

/// <summary>Tek bir gönderim turunun sonucu.</summary>
public sealed record SyncRunResult(bool Configured, int SentCuts, SyncException? Failure)
{
    public bool Succeeded => Configured && Failure is null;
}
