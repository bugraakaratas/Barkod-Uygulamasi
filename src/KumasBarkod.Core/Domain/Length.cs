using System.Globalization;

namespace KumasBarkod.Core.Domain;

/// <summary>
/// Metre girişi. Değer santim (tam sayı) olarak tutulur; kayan nokta kullanılmaz.
/// </summary>
public static class Length
{
    public const int MaxDecimals = 2;

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>
    /// Operatör girdisini santime çevirir. Ondalık ayırıcı olarak virgül de nokta da kabul edilir.
    /// Binlik ayırıcı kabul edilmez ("1.234,5" reddedilir): metrede belirsizlik yaratır.
    /// En fazla 2 ondalık; fazlası yuvarlanmaz, reddedilir.
    /// </summary>
    public static bool TryParseMeters(string? input, long maxCm, out long cm, out string error)
    {
        cm = 0;
        var text = input?.Trim() ?? "";

        if (text.Length == 0)
        {
            error = "Metre girin.";
            return false;
        }

        var separatorIndex = text.IndexOfAny([',', '.']);
        var intPart = separatorIndex < 0 ? text : text[..separatorIndex];
        var fracPart = separatorIndex < 0 ? "" : text[(separatorIndex + 1)..];

        if (!IsDigits(intPart) || !IsDigits(fracPart) || intPart.Length + fracPart.Length == 0)
        {
            error = "Metre yalnızca rakam ve tek bir virgül/nokta içerebilir (ör. 25,50).";
            return false;
        }

        if (fracPart.Length > MaxDecimals)
        {
            error = "Metre en fazla 2 ondalık basamak olabilir (ör. 25,50).";
            return false;
        }

        if (intPart.Length > 6)
        {
            error = $"Metre en fazla {Format(maxCm)} olabilir.";
            return false;
        }

        long whole = intPart.Length == 0 ? 0 : long.Parse(intPart, CultureInfo.InvariantCulture);
        long frac = fracPart.Length == 0 ? 0 : long.Parse(fracPart.PadRight(MaxDecimals, '0'), CultureInfo.InvariantCulture);
        var value = whole * 100 + frac;

        if (value <= 0)
        {
            error = "Metre 0'dan büyük olmalı.";
            return false;
        }

        if (value > maxCm)
        {
            error = $"Metre en fazla {Format(maxCm)} olabilir. Değer doğruysa üst sınırı Ayarlar'dan değiştirin.";
            return false;
        }

        cm = value;
        error = "";
        return true;
    }

    /// <summary>2550 → "25,50"</summary>
    public static string Format(long cm) => (cm / 100m).ToString("0.00", Turkish);

    private static bool IsDigits(string s)
    {
        foreach (var ch in s)
            if (ch is < '0' or > '9') return false;
        return true;
    }
}
