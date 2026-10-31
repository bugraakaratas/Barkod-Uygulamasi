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

    /// <summary>
    /// Okutulan veya elle yazılan girdiyi kesim ID'sine çevirir. Kabul edilenler:
    /// <c>K1000123</c> (barkod), <c>K1-000123</c>, küçük harf, baştaki/sondaki boşluk.
    /// Türkçe klavye toleransı: <c>ı/i/İ</c> → <c>I</c>; ABD düzenindeki okuyucunun tiresi
    /// Türkçe Q'da <c>*</c> çıkar, o da ayırıcı sayılır.
    /// </summary>
    public static bool TryParseScan(string? input, out string cutId)
    {
        cutId = "";
        if (input is null) return false;

        Span<char> buffer = stackalloc char[16];
        var length = 0;
        foreach (var raw in input.Trim())
        {
            if (raw is '-' or '*' or '_' or ' ') continue;
            if (length == buffer.Length) return false;
            buffer[length++] = raw switch
            {
                'ı' or 'i' or 'İ' => 'I',
                _ => char.ToUpperInvariant(raw),
            };
        }

        if (length != 2 + SequenceDigits) return false;

        var station = new string(buffer[..2]);
        var digits = buffer[2..length];
        if (!Stations.IsValidCode(station)) return false;
        foreach (var ch in digits)
            if (ch is < '0' or > '9') return false;

        var sequence = long.Parse(digits);
        if (sequence < 1) return false;

        cutId = Format(station, sequence);
        return true;
    }
}

public static partial class Stations
{
    public const string DefaultCode = "K1";

    /// <summary>İki karakter: büyük harf + büyük harf/rakam (K1, K2, A3, KB...).</summary>
    public static bool IsValidCode(string? code) => code is not null && CodeRegex().IsMatch(code);

    [GeneratedRegex("^[A-Z][A-Z0-9]$")]
    private static partial Regex CodeRegex();
}
