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

    // Etiket yerleşimi (nokta, 8 nokta = 1 mm). Ekran önizlemesi de bunları kullanır.
    public const int BarcodeX = Margin, BarcodeY = 16, BarcodeHeight = 80, ModuleDots = 2;
    public const int IdY = 106, IdFont = 44;
    public const int LotY = 158;
    public const int LengthY = 198, LengthFont = 32;
    public const int DateY = 206, DateFont = 22;
    public const int TextX = Margin;
    public const int TextWidth = ContentWidth;

    /// <summary>Lot satırı ve yazı boyutu (uzun lotta küçülür/kırpılır).</summary>
    public static (int Font, string Text) LotLine(string lotCode) => FitLot("Lot: " + lotCode);

    public static string DateText(DateTime createdAtLocal) => createdAtLocal.ToString("dd.MM.yyyy HH:mm", Turkish);

    public string RenderText(LabelData label)
    {
        var (lotFont, lotText) = LotLine(label.LotCode);

        var sb = new StringBuilder();
        sb.Append("^XA\n");
        sb.Append("^CI28\n");                                  // UTF-8: Türkçe karakterler
        sb.Append($"^PW{WidthDots}\n^LL{HeightDots}\n^LH0,0\n");

        // Barkod: modül 2 nokta, yükseklik 80 nokta (10 mm), okunur satır kapalı (ID altta büyük basılır),
        // mod A = otomatik alt küme seçimi (rakamlar sıkıştırılır, barkod kısalır).
        sb.Append($"^FO{BarcodeX},{BarcodeY}^BY{ModuleDots},3,{BarcodeHeight}^BCN,{BarcodeHeight},N,N,N,A^FD{label.Barcode}^FS\n");

        sb.Append($"^FO{TextX},{IdY}^A0N,{IdFont},{IdFont}^FD{label.CutId}^FS\n");
        sb.Append($"^FO{TextX},{LotY}^A0N,{lotFont},{lotFont}^FH_^FD{Escape(lotText)}^FS\n");
        sb.Append($"^FO{TextX},{LengthY}^A0N,{LengthFont},{LengthFont}^FD{Length.Format(label.LengthCm)} m^FS\n");
        sb.Append($"^FO{TextX},{DateY}^A0N,{DateFont},{DateFont}^FB{ContentWidth},1,0,R^FD{DateText(label.CreatedAtLocal)}^FS\n");

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
