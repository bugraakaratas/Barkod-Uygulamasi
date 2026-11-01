using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using ZXing;
using ZXing.Common;
using ZXing.OneD;

namespace KumasBarkod.Tests.Printing;

/// <summary>
/// Üretilen barkod gerçekten okunabiliyor mu? Modüller gri tonlu bir görüntüye çizilir
/// (sessiz bölge + modül başına 3 piksel) ve bağımsız bir Code128 OKUYUCU ile çözülür.
/// </summary>
public class Code128Tests
{
    private static string Decode(bool[] modules, int pixelsPerModule = 3, int height = 40)
    {
        var quiet = Code128.QuietZoneModules * pixelsPerModule;
        var width = modules.Length * pixelsPerModule + 2 * quiet;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);
        for (var m = 0; m < modules.Length; m++)
        {
            if (!modules[m]) continue;
            for (var y = 0; y < height; y++)
                for (var p = 0; p < pixelsPerModule; p++)
                    pixels[y * width + quiet + m * pixelsPerModule + p] = 0;
        }

        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        var result = new Code128Reader().decode(new BinaryBitmap(new HybridBinarizer(source)));
        return result.Text;
    }

    [Theory]
    [InlineData("K1000001")]
    [InlineData("K1000123")]
    [InlineData("K2999999")]
    [InlineData("AB012345")]
    public void Encoded_barcode_decodes_back_to_same_text(string barcode)
    {
        Assert.Equal(barcode, Decode(Code128.Encode(barcode)));
    }

    [Fact]
    public void Real_cut_id_barcode_round_trips_to_cut_id()
    {
        var cutId = CutIds.Format("K1", 4521);
        var decoded = Decode(Code128.Encode(CutIds.ToBarcode(cutId)));

        Assert.True(CutIds.TryParseScan(decoded, out var parsed));
        Assert.Equal(cutId, parsed);
    }

    [Fact]
    public void Barcode_starts_and_ends_with_a_bar_and_has_stop_pattern()
    {
        var modules = Code128.Encode("K1000123");

        Assert.True(modules[0]);
        Assert.True(modules[^1]);
        // Stop deseni 2331112 → son 2 modül siyah, önündeki 3 beyaz değil: "11" ile biter (siyah çubuk 2 modül)
        Assert.True(modules[^2]);
    }

    [Fact]
    public void Digits_are_compressed_so_barcode_fits_50mm_label()
    {
        // B kümesi 2 karakter + C kümesi 3 çift + başlangıç, geçiş, sağlama, bitiş ≈ 90 modül.
        // Etikette modül 2 nokta (0,25 mm) → ~23 mm + sessiz bölgeler; 50 mm etikete rahat sığar.
        var modules = Code128.Encode("K1000123");
        Assert.InRange(modules.Length, 80, 110);
    }

    [Fact]
    public void Empty_input_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Code128.Encode(""));
    }
}
