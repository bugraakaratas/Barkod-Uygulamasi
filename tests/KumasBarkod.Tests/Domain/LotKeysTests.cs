using KumasBarkod.Core.Domain;

namespace KumasBarkod.Tests.Domain;

public class LotKeysTests
{
    [Theory]
    [InlineData("LOT-3", "LOT3")]
    [InlineData("lot 3", "LOT3")]
    [InlineData(" Lot_3 ", "LOT3")]
    [InlineData("lot/3.", "LOT3")]
    [InlineData("Akın Tekstil", "AKINTEKSTIL")]
    [InlineData("AKIN TEKSTİL", "AKINTEKSTIL")]
    [InlineData("akin-tekstil", "AKINTEKSTIL")]
    [InlineData("Şahin Çözgü Örme Ğ Ü", "SAHINCOZGUORMEGU")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Key_ignores_only_spelling(string? input, string expected) => Assert.Equal(expected, LotKeys.Key(input));

    [Fact]
    public void Key_keeps_different_numbers_apart()
    {
        Assert.NotEqual(LotKeys.Key("LOT-3"), LotKeys.Key("LOT-03"));
        Assert.NotEqual(LotKeys.Key("LOT-3"), LotKeys.Key("LOT-30"));
    }

    [Theory]
    [InlineData("LOT-003", "LOT3")]
    [InlineData("LOT-3", "LOT3")]
    [InlineData("A01B002", "A1B2")]
    [InlineData("LOT-100", "LOT100")]
    [InlineData("LOT-0", "LOT0")]
    [InlineData("000", "0")]
    [InlineData("A0B", "A0B")]
    public void Loose_key_also_ignores_leading_zeros(string input, string expected) => Assert.Equal(expected, LotKeys.LooseKey(input));
}
