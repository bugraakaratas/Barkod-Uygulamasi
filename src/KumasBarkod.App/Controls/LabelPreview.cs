using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KumasBarkod.Core.Domain;
using KumasBarkod.Core.Printing;
using Z = KumasBarkod.Core.Printing.ZplLabelTemplate;

namespace KumasBarkod.App.Controls;

/// <summary>
/// Etiketin birebir görüntüsü (50 × 30 mm). Yerleşim ZPL şablonuyla aynı sabitlerden gelir;
/// barkod gerçek Code128'dir (okuyucu ya da telefon uygulaması ekrandan/kağıttan okuyabilir).
/// Ekranda en-boy oranı korunarak ölçeklenir; yazdırırken gerçek boyutta (50 × 30 mm) çizilir.
/// </summary>
public sealed class LabelPreview : FrameworkElement
{
    private const double LabelWidthDots = Z.WidthDots;    // 400
    private const double LabelHeightDots = Z.HeightDots;  // 240

    /// <summary>1 mm'nin WPF birimi (1/96 inç) karşılığı.</summary>
    public const double DipPerMm = 96.0 / 25.4;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(LabelData), typeof(LabelPreview),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((LabelPreview)d)._modules = null));

    /// <summary>
    /// Ekranda true: barkod modülleri tam piksele (en az 2 piksel) oturtulur. Küçük ölçekte
    /// orantılı çizimde modüller 1–2 piksel arasında yuvarlanıp genişlik oranları bozuluyor ve
    /// barkod OKUNMUYORDU (testte görüldü). Yazdırırken false: gerçek boyut (0,25 mm modül).
    /// </summary>
    public static readonly DependencyProperty SnapBarcodeToPixelsProperty = DependencyProperty.Register(
        nameof(SnapBarcodeToPixels), typeof(bool), typeof(LabelPreview),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowEdgeProperty = DependencyProperty.Register(
        nameof(ShowEdge), typeof(bool), typeof(LabelPreview),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface Font = new(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FontBold = new(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private bool[]? _modules;

    public LabelPreview()
    {
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);   // çubuk kenarları keskin
    }

    public LabelData? Label
    {
        get => (LabelData?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool SnapBarcodeToPixels
    {
        get => (bool)GetValue(SnapBarcodeToPixelsProperty);
        set => SetValue(SnapBarcodeToPixelsProperty, value);
    }

    /// <summary>Etiket kenar çizgisi (ekranda var, yazdırırken kapatılabilir).</summary>
    public bool ShowEdge
    {
        get => (bool)GetValue(ShowEdgeProperty);
        set => SetValue(ShowEdgeProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? LabelWidthDots : available.Width;
        var height = width * LabelHeightDots / LabelWidthDots;
        if (!double.IsInfinity(available.Height) && height > available.Height)
        {
            height = available.Height;
            width = height * LabelWidthDots / LabelHeightDots;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final) => MeasureOverride(final);

    protected override void OnRender(DrawingContext dc)
    {
        var scale = ActualWidth / LabelWidthDots;
        if (scale <= 0) return;
        dc.PushTransform(new ScaleTransform(scale, scale));

        var edge = ShowEdge ? new Pen(new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8)), 1.5 / scale) : null;
        dc.DrawRoundedRectangle(Brushes.White, edge, new Rect(0, 0, LabelWidthDots, LabelHeightDots), 10, 10);

        var label = Label;
        if (label is null)
        {
            DrawText(dc, "Etiket önizlemesi", Font, 22, Brushes.Gray, LabelWidthDots / 2, LabelHeightDots / 2 - 12, TextAlignment.Center);
            dc.Pop();
            return;
        }

        _modules ??= Code128.Encode(label.Barcode);
        if (!SnapBarcodeToPixels)
            DrawBars(dc, _modules, Z.BarcodeX, Z.BarcodeY, Z.ModuleDots, Z.BarcodeHeight);

        var (lotFont, lotText) = Z.LotLine(label.LotCode);
        DrawText(dc, label.CutId, FontBold, Z.IdFont, Brushes.Black, Z.TextX, Z.IdY);
        DrawText(dc, lotText, Font, lotFont, Brushes.Black, Z.TextX, Z.LotY);
        DrawText(dc, Length.Format(label.LengthCm) + " m", FontBold, Z.LengthFont, Brushes.Black, Z.TextX, Z.LengthY);
        DrawText(dc, Z.DateText(label.CreatedAtLocal), Font, Z.DateFont, Brushes.Black,
            Z.TextX + Z.TextWidth, Z.DateY, TextAlignment.Right);

        dc.Pop();

        if (SnapBarcodeToPixels)
            DrawSnappedBarcode(dc, _modules, scale);
    }

    /// <summary>Ardışık siyah modüller tek dikdörtgen (keskin çizim). Birimler çağıranın koordinatında.</summary>
    private static void DrawBars(DrawingContext dc, bool[] modules, double x, double y, double moduleWidth, double height)
    {
        for (var i = 0; i < modules.Length; i++)
        {
            if (!modules[i]) continue;
            var start = i;
            while (i + 1 < modules.Length && modules[i + 1]) i++;
            dc.DrawRectangle(Brushes.Black, null, new Rect(x + start * moduleWidth, y, (i - start + 1) * moduleWidth, height));
        }
    }

    /// <summary>
    /// Ekran: modül genişliği tam piksel (en az 2). Gerekirse barkod etikette orantılıdan biraz
    /// geniş çizilir; sol kenar boşluğu en az 10 modül (okuyucu için sessiz bölge).
    /// </summary>
    private void DrawSnappedBarcode(DrawingContext dc, bool[] modules, double scale)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var modulePx = Math.Max(2, Math.Round(Z.ModuleDots * scale * dpi));
        var moduleDip = modulePx / dpi;

        // Sol kenar: etiketteki konum, ama en az sessiz bölge kadar; tam piksele yuvarla
        var leftDip = Math.Max(Z.BarcodeX * scale, Code128.QuietZoneModules * moduleDip);
        leftDip = Math.Round(leftDip * dpi) / dpi;

        // Etikete sığmıyorsa (çok dar önizleme) orantılı çizime dön
        if (leftDip + modules.Length * moduleDip > ActualWidth)
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            DrawBars(dc, modules, Z.BarcodeX, Z.BarcodeY, Z.ModuleDots, Z.BarcodeHeight);
            dc.Pop();
            return;
        }

        var top = Math.Round(Z.BarcodeY * scale * dpi) / dpi;
        DrawBars(dc, modules, leftDip, top, moduleDip, Z.BarcodeHeight * scale);
    }

    /// <summary>ZPL'deki gibi: (x, y) metin kutusunun sol üstü; yükseklik nokta cinsinden.</summary>
    private void DrawText(DrawingContext dc, string text, Typeface face, double heightDots, Brush brush,
        double x, double y, TextAlignment align = TextAlignment.Left)
    {
        var ft = new FormattedText(text, CultureInfo.GetCultureInfo("tr-TR"), FlowDirection.LeftToRight,
            face, heightDots * 0.92, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            TextAlignment = align,
        };
        dc.DrawText(ft, new Point(x, y));
    }
}
