using KumasBarkod.Core.Domain;

namespace KumasBarkod.Tests.Domain;

public class CutIdsTests
{
    [Theory]
    [InlineData("K1", 1, "K1-000001")]
    [InlineData("K1", 123, "K1-000123")]
    [InlineData("K2", 999999, "K2-999999")]
    public void Format_pads_sequence_to_six_digits(string station, long seq, string expected)
    {
        Assert.Equal(expected, CutIds.Format(station, seq));
    }

    [Fact]
    public void Barcode_has_no_dash_and_fixed_length()
    {
        Assert.Equal("K1000123", CutIds.ToBarcode("K1-000123"));
        Assert.Equal(8, CutIds.ToBarcode(CutIds.Format("K9", 5)).Length);
    }

    [Theory]
    [InlineData("k1")]
    [InlineData("K")]
    [InlineData("K12")]
    [InlineData("1K")]
    [InlineData("K-")]
    [InlineData("")]
    public void Invalid_station_codes_are_rejected(string station)
    {
        Assert.False(Stations.IsValidCode(station));
        Assert.Throws<ArgumentException>(() => CutIds.Format(station, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000)]
    public void Sequence_out_of_range_is_rejected(long seq)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CutIds.Format("K1", seq));
    }
}
