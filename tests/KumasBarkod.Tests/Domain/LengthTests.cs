using KumasBarkod.Core.Domain;

namespace KumasBarkod.Tests.Domain;

public class LengthTests
{
    private const long Max = 300_00; // 300 m

    [Theory]
    [InlineData("25,50", 2550)]
    [InlineData("25.50", 2550)]
    [InlineData("25,5", 2550)]
    [InlineData("25.5", 2550)]
    [InlineData("25", 2500)]
    [InlineData("  7  ", 700)]
    [InlineData("0,01", 1)]
    [InlineData(",5", 50)]
    [InlineData("5,", 500)]
    [InlineData("300", 30000)]      // tam sınır kabul
    [InlineData("007,10", 710)]
    public void Valid_inputs_are_parsed_to_centimeters(string input, long expectedCm)
    {
        Assert.True(Length.TryParseMeters(input, Max, out var cm, out var error), error);
        Assert.Equal(expectedCm, cm);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("0,00")]
    [InlineData("-3")]
    [InlineData("+3")]
    [InlineData("abc")]
    [InlineData("12a")]
    [InlineData(",")]
    [InlineData("1,234")]           // 3 ondalık: yuvarlanmaz, reddedilir
    [InlineData("1.234,5")]         // binlik ayırıcı belirsiz
    [InlineData("1,2,3")]
    [InlineData("1e3")]
    [InlineData("25 50")]
    [InlineData("300,01")]          // sınırın üstü
    [InlineData("9999999")]
    public void Invalid_inputs_are_rejected_with_message(string? input)
    {
        Assert.False(Length.TryParseMeters(input, Max, out var cm, out var error));
        Assert.Equal(0, cm);
        Assert.False(string.IsNullOrWhiteSpace(error));
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
