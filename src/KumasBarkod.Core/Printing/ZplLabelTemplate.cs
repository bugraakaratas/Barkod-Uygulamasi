using System.Globalization;
using System.Text;
using KumasBarkod.Core.Domain;

namespace KumasBarkod.Core.Printing;

/// <summary>
/// Zebra ZPL etiket şablonu. Varsayılan: 50 × 30 mm etiket, 203 dpi (8 nokta/mm).
/// Barkod yazıcının kendi Code128 komutuyla basılır (keskin, ek kütüphane yok).
///
///  ┌──────────────────────────────┐
///  │ ║│║║│║│║║│║║│║ (Code128)     │  K1000123 (tiresiz)
///  │ K1-000123                    │  büyük, okunur ID
///  │ Lot: LOT-A                   │
///  │ 25,50 m        01.11.2026 09:30│
///  └──────────────────────────────┘
/// </summary>
public sealed class ZplLabelTemplate : ILabelTemplate
{
    public const int DotsPerMm = 8;
    public const int WidthDots = 50 * DotsPerMm;   // 400
    public const int HeightDots = 30 * DotsPerMm;  // 240
    private const int Margin = 24;                 // 3 mm; Code128 sessiz bölgesi (10 modül × 2) için yeterli
    private const int ContentWidth = WidthDots - 2 * Margin;

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public byte[] Render(LabelData label) => Encoding.UTF8.GetBytes(RenderText(label));

    public string RenderText(LabelData label)
    {
        var lotLine = "Lot: " + label.LotCode;
        var (lotFont, lotText) = FitLot(lotLine);

        var sb = new StringBuilder();
        sb.Append("^XA\n");
        sb.Append("^CI28\n");                                  // UTF-8: Türkçe karakterler
        sb.Append($"^PW{WidthDots}\n^LL{HeightDots}\n^LH0,0\n");

        // Barkod: modül 2 nokta, yükseklik 80 nokta (10 mm), okunur satır kapalı (ID altta büyük basılır),
        // mod A = otomatik alt küme seçimi (rakamlar sıkıştırılır, barkod kısalır).
        sb.Append($"^FO{Margin},16^BY2,3,80^BCN,80,N,N,N,A^FD{label.Barcode}^FS\n");

        sb.Append($"^FO{Margin},106^A0N,44,44^FD{label.CutId}^FS\n");
        sb.Append($"^FO{Margin},158^A0N,{lotFont},{lotFont}^FH_^FD{Escape(lotText)}^FS\n");
        sb.Append($"^FO{Margin},198^A0N,32,32^FD{Length.Format(label.LengthCm)} m^FS\n");
        sb.Append($"^FO{Margin},206^A0N,22,22^FB{ContentWidth},1,0,R^FD{label.CreatedAtLocal.ToString("dd.MM.yyyy HH:mm", Turkish)}^FS\n");

        sb.Append("^PQ1\n");
        sb.Append("^XZ\n");
        return sb.ToString();
    }

    /// <summary>Uzun lotta yazıyı küçült; yine sığmazsa kırp (asıl bilgi barkod ve ID'dedir).</summary>
    private static (int Font, string Text) FitLot(string text)
    {
        if (text.Length <= 20) return (30, text);
        if (text.Length <= 28) return (22, text);
        return (22, text[..25] + "...");
    }

    /// <summary>
    /// ^FH_ ile alan içi onaltılık kaçış: ZPL komut karakterleri (^ ~) ve kaçış karakteri (_)
    /// lot metninde geçerse etiket bozulmasın.
    /// </summary>
    public static string Escape(string text) => text
        .Replace("_", "_5F", StringComparison.Ordinal)
        .Replace("^", "_5E", StringComparison.Ordinal)
        .Replace("~", "_7E", StringComparison.Ordinal);
}
