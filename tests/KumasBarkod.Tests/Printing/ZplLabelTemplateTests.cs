using System.Text;
using KumasBarkod.Core.Printing;

namespace KumasBarkod.Tests.Printing;

public class ZplLabelTemplateTests
{
    private readonly ZplLabelTemplate _template = new();

    private static LabelData Sample(string lot = "LOT-A", long cm = 2550) =>
        new("K1-000123", "K1000123", lot, cm, new DateTime(2026, 11, 1, 9, 30, 0), "K1");

    /// <summary>
    /// Sabit referans çıktı. Şablon bilerek değiştirilirse bu test de güncellenir;
    /// yanlışlıkla değişirse yakalanır.
    /// </summary>
    [Fact]
    public void Renders_expected_zpl()
    {
        const string expected =
            "^XA\n" +
            "^CI28\n" +
            "^PW400\n^LL240\n^LH0,0\n" +
            "^FO24,16^BY2,3,80^BCN,80,N,N,N,A^FDK1000123^FS\n" +
            "^FO24,106^A0N,44,44^FDK1-000123^FS\n" +
            "^FO24,158^A0N,30,30^FH_^FDLot: LOT-A^FS\n" +
            "^FO24,198^A0N,32,32^FD25,50 m^FS\n" +
            "^FO24,206^A0N,22,22^FB352,1,0,R^FD01.11.2026 09:30^FS\n" +
            "^PQ1\n" +
            "^XZ\n";

        Assert.Equal(expected, _template.RenderText(Sample()));
    }

    [Fact]
    public void Barcode_field_contains_dashless_id_only()
    {
        var zpl = _template.RenderText(Sample());
        Assert.Contains("^BCN,80,N,N,N,A^FDK1000123^FS", zpl);
    }

    [Fact]
    public void Output_is_one_complete_label()
    {
        var zpl = _template.RenderText(Sample());
        Assert.StartsWith("^XA", zpl);
        Assert.EndsWith("^XZ\n", zpl);
        Assert.Single(AllIndexes(zpl, "^XA"));
        Assert.Single(AllIndexes(zpl, "^XZ"));
    }

    [Fact]
    public void Turkish_characters_are_sent_as_utf8()
    {
        var bytes = _template.Render(Sample(lot: "YENİ-ŞĞÜÇÖı"));
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Contains("^CI28", text);
        Assert.Contains("Lot: YENİ-ŞĞÜÇÖı", text);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), bytes.Length);
    }

    [Theory]
    [InlineData("A^B", "A_5EB")]
    [InlineData("A~B", "A_7EB")]
    [InlineData("A_B", "A_5FB")]
    [InlineData("^XZ", "_5EXZ")]       // lot metni etiketi erken bitiremez
    public void Zpl_control_characters_in_lot_are_escaped(string lot, string escaped)
    {
        var zpl = _template.RenderText(Sample(lot: lot));

        Assert.Contains($"^FH_^FDLot: {escaped}^FS", zpl);
        Assert.Single(AllIndexes(zpl, "^XZ"));
    }

    [Fact]
    public void Medium_lot_uses_smaller_font()
    {
        var zpl = _template.RenderText(Sample(lot: "LOT-2026-ABCDEFG")); // "Lot: " + 16 = 21
        Assert.Contains("^A0N,22,22^FH_^FDLot: LOT-2026-ABCDEFG^FS", zpl);
    }

    [Fact]
    public void Very_long_lot_is_truncated()
    {
        var zpl = _template.RenderText(Sample(lot: new string('X', 50)));
        Assert.Contains("^FDLot: " + new string('X', 20) + "...^FS", zpl);
    }

    [Theory]
    [InlineData(1, "0,01 m")]
    [InlineData(30000, "300,00 m")]
    public void Length_is_printed_with_turkish_decimal(long cm, string expected)
    {
        Assert.Contains($"^FD{expected}^FS", _template.RenderText(Sample(cm: cm)));
    }

    private static List<int> AllIndexes(string s, string value)
    {
        var list = new List<int>();
        for (var i = s.IndexOf(value, StringComparison.Ordinal); i >= 0; i = s.IndexOf(value, i + 1, StringComparison.Ordinal))
            list.Add(i);
        return list;
    }
}
