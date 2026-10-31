using KumasBarkod.Core.Domain;

namespace KumasBarkod.Tests.Domain;

public class ScanParsingTests
{
    [Theory]
    [InlineData("K1000123", "K1-000123")]     // barkod
    [InlineData("K1-000123", "K1-000123")]    // ekrandaki biçim
    [InlineData("k1000123", "K1-000123")]     // küçük harf
    [InlineData("  K1000123  ", "K1-000123")]
    [InlineData("K1*000123", "K1-000123")]    // ABD düzenli okuyucu + Türkçe Q: tire → *
    [InlineData("Kı000123", "KI-000123")]     // Türkçe Q'da küçük i → ı
    [InlineData("Kİ000123", "KI-000123")]
    [InlineData("K2999999", "K2-999999")]
    [InlineData("AB000001", "AB-000001")]
    public void Valid_inputs_are_parsed(string input, string expected)
    {
        Assert.True(CutIds.TryParseScan(input, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K100012")]       // 5 hane
    [InlineData("K10001234")]     // 7 hane
    [InlineData("K1000000")]      // sıra 0
    [InlineData("1K000123")]      // istasyon rakamla başlıyor
    [InlineData("K1A00123")]
    [InlineData("LOT-A")]
    [InlineData("25,50")]
    [InlineData("K1000123K1000123")] // iki kez okutulmuş
    public void Invalid_inputs_are_rejected(string? input)
    {
        Assert.False(CutIds.TryParseScan(input, out _));
    }

    [Fact]
    public void Barcode_round_trips()
    {
        var id = CutIds.Format("K7", 4521);
        Assert.True(CutIds.TryParseScan(CutIds.ToBarcode(id), out var parsed));
        Assert.Equal(id, parsed);
    }
}
