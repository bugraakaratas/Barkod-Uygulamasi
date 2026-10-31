using System.Text.RegularExpressions;

namespace KumasBarkod.Core.Domain;

/// <summary>
/// Kesim ID biçimi. Saklanan ve ekranda görünen: <c>K1-000123</c>.
/// Barkoda basılan: <c>K1000123</c> (Türkçe klavyede tire sorunu olmasın diye tiresiz).
/// </summary>
public static partial class CutIds
{
    public const int SequenceDigits = 6;
    public const long MaxSequence = 999_999;

    public static string Format(string stationCode, long sequenceNo)
    {
        if (!Stations.IsValidCode(stationCode))
            throw new ArgumentException($"Geçersiz istasyon kodu: '{stationCode}'", nameof(stationCode));
        if (sequenceNo is < 1 or > MaxSequence)
            throw new ArgumentOutOfRangeException(nameof(sequenceNo), sequenceNo, "Sıra numarası 1–999999 olmalı.");

        return $"{stationCode}-{sequenceNo:D6}";
    }

    public static string ToBarcode(string cutId) => cutId.Replace("-", "", StringComparison.Ordinal);
}

public static partial class Stations
{
    public const string DefaultCode = "K1";

    /// <summary>İki karakter: büyük harf + büyük harf/rakam (K1, K2, A3, KB...).</summary>
    public static bool IsValidCode(string? code) => code is not null && CodeRegex().IsMatch(code);

    [GeneratedRegex("^[A-Z][A-Z0-9]$")]
    private static partial Regex CodeRegex();
}
