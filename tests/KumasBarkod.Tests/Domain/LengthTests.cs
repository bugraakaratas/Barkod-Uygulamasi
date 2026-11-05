using KumasBarkod.Core.Domain;

namespace KumasBarkod.Tests.Domain;

public class LengthTests
{
    private const long Max = 300_00; // 30000 cm = 300 m

    [Theory]
    [InlineData("2550", 2550)]
    [InlineData("  700  ", 700)]
    [InlineData("1", 1)]
    [InlineData("25", 25)]          // metre alışkanlığı: 25 cm'dir, canlı ipucu "= 0,25 m" gösterir
    [InlineData("007", 7)]
    [InlineData("30000", 30000)]    // tam sınır kabul
    public void Valid_inputs_are_whole_centimeters(string input, long expectedCm)
    {
        Assert.True(Length.TryParseCentimeters(input, Max, out var cm, out var error), error);
        Assert.Equal(expectedCm, cm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("000")]
    [InlineData("-3")]
    [InlineData("+3")]
    [InlineData("abc")]
    [InlineData("12a")]
    [InlineData("25,50")]           // eski metre biçimi: sessizce çevrilmez
    [InlineData("25.50")]
    [InlineData("25,")]
    [InlineData(",5")]
    [InlineData("1.234")]
    [InlineData("1e3")]
    [InlineData("25 50")]
    [InlineData("30001")]           // sınırın üstü
    [InlineData("9999999999999999999999")]  // long'a sığmaz, taşma hatası olmamalı
    public void Invalid_inputs_are_rejected_with_message(string? input)
    {
        Assert.False(Length.TryParseCentimeters(input, Max, out var cm, out var error));
        Assert.Equal(0, cm);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Decimal_input_error_shows_centimeter_example()
    {
        Length.TryParseCentimeters("25,50", Max, out _, out var error);
        Assert.Contains("cm", error);
        Assert.Contains("2550", error);
    }

    [Fact]
    public void FormatCm_is_digits_only()
    {
        Assert.Equal("30000", Length.FormatCm(30000));
    }

    [Theory]
    [InlineData(2550, "25,50")]
    [InlineData(1, "0,01")]
    [InlineData(30000, "300,00")]
    public void Format_uses_turkish_decimal_comma(long cm, string expected)
    {
        Assert.Equal(expected, Length.Format(cm));
    }

    [Theory]
    [InlineData(445904735, "4.459.047,35")]
    [InlineData(2550, "25,50")]
    [InlineData(0, "0,00")]
    public void FormatGrouped_uses_thousand_separators(long cm, string expected)
    {
        Assert.Equal(expected, Length.FormatGrouped(cm));
    }
}
