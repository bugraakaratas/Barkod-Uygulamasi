using System.Globalization;

namespace KumasBarkod.Core.Domain;

/// <summary>
/// Uzunluk girişi. Operatör santim (tam sayı) girer, değer de santim tutulur; kayan nokta kullanılmaz.
/// Gösterim (etiket, listeler, toplamlar) metredir.
/// </summary>
public static class Length
{
    private const int MaxInputDigits = 9;

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>
    /// Operatör girdisini santime çevirir. Yalnızca pozitif tam sayı kabul edilir:
    /// virgül/nokta reddedilir, çünkü eski metre alışkanlığıyla yazılan "25,50" sessizce
    /// başka bir değere dönüşmemelidir.
    /// </summary>
    public static bool TryParseCentimeters(string? input, long maxCm, out long cm, out string error)
    {
        cm = 0;
        var text = input?.Trim() ?? "";

        if (text.Length == 0)
        {
            error = "Uzunluğu cm olarak girin.";
            return false;
        }

        if (text.IndexOfAny([',', '.']) >= 0)
        {
            error = "Uzunluk cm cinsinden tam sayı olmalı; virgül veya nokta kullanılmaz (ör. 2550).";
            return false;
        }

        if (!IsDigits(text))
        {
            error = "Uzunluk yalnızca rakam içerebilir (ör. 2550).";
            return false;
        }

        var digits = text.TrimStart('0');
        if (digits.Length == 0)
        {
            error = "Uzunluk 0'dan büyük olmalı.";
            return false;
        }

        if (digits.Length > MaxInputDigits || long.Parse(digits, CultureInfo.InvariantCulture) > maxCm)
        {
            error = $"Uzunluk en fazla {FormatCm(maxCm)} cm olabilir. Değer doğruysa üst sınırı Ayarlar'dan değiştirin.";
            return false;
        }

        cm = long.Parse(digits, CultureInfo.InvariantCulture);
        error = "";
        return true;
    }

    /// <summary>2550 → "2550" (giriş biçimi: yalnızca rakam)</summary>
    public static string FormatCm(long cm) => cm.ToString(CultureInfo.InvariantCulture);

    /// <summary>2550 → "25,50" (etiket ve giriş: binlik ayırıcı yok, girişle aynı biçim)</summary>
    public static string Format(long cm) => (cm / 100m).ToString("0.00", Turkish);

    /// <summary>445904735 → "4.459.047,35" (büyük toplamlar için, binlik ayırıcılı)</summary>
    public static string FormatGrouped(long cm) => (cm / 100m).ToString("N2", Turkish);

    private static bool IsDigits(string s)
    {
        foreach (var ch in s)
            if (ch is < '0' or > '9') return false;
        return true;
    }
}
