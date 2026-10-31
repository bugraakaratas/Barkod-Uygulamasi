namespace KumasBarkod.Core.Domain;

public static class CutStatus
{
    public const string Active = "active";
    public const string Voided = "voided";
}

/// <summary>Bir kesim kaydı; <c>cuts</c> tablosunun bir satırı.</summary>
public sealed class Cut
{
    public required string CutId { get; init; }
    public required string StationCode { get; init; }
    public required long SequenceNo { get; init; }
    public required string LotCode { get; init; }
    public required long LengthCm { get; init; }
    public string? Operator { get; init; }
    public required string Status { get; init; }
    public string? VoidReason { get; init; }
    public string? VoidedAt { get; init; }
    /// <summary>UTC, <see cref="Timestamps"/> biçiminde.</summary>
    public required string CreatedAt { get; init; }
    public long PrintCount { get; init; }
    public string? LastPrintedAt { get; init; }

    /// <summary>Barkoda basılan tiresiz biçim: K1000123</summary>
    public string Barcode => CutIds.ToBarcode(CutId);

    public bool IsVoided => Status == CutStatus.Voided;

    /// <summary>Dapper için SELECT listesi: snake_case sütunları özelliklere eşler.</summary>
    internal const string SelectColumns = """
        cut_id AS CutId, station_code AS StationCode, sequence_no AS SequenceNo,
        lot_code AS LotCode, length_cm AS LengthCm, operator AS Operator, status AS Status,
        void_reason AS VoidReason, voided_at AS VoidedAt, created_at AS CreatedAt,
        print_count AS PrintCount, last_printed_at AS LastPrintedAt
        """;
}

/// <summary>Kullanıcıya gösterilecek doğrulama hatası (mesaj Türkçe, ekranda gösterilir).</summary>
public sealed class CutValidationException(string message) : Exception(message);
