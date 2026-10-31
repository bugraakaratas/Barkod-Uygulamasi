using System.Globalization;

namespace KumasBarkod.Core.Domain;

/// <summary>
/// Zaman damgaları UTC ve sabit uzunlukta saklanır (2026-11-01T09:30:00.000Z);
/// böylece metin sıralaması kronolojik sıralamayla aynı olur.
/// </summary>
public static class Timestamps
{
    private const string FormatString = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString(FormatString, CultureInfo.InvariantCulture);

    public static DateTime Parse(string value) =>
        DateTime.ParseExact(value, FormatString, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
