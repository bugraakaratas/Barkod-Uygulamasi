using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KumasBarkod.App.Controls;
using KumasBarkod.Core.Data;
using KumasBarkod.Core.Printing;

namespace KumasBarkod.App.Printing;

/// <summary>
/// Gösterim modu: hiçbir şey basılmaz, etiket yalnızca ekranda önizlenir.
/// Yazıcı ve okuyucu olmadan sistemi uçtan uca göstermek için (varsayılan).
/// </summary>
public sealed class ScreenOnlyPrinter : ILabelPrinter
{
    public string DisplayName => "Gösterim (yalnızca ekran)";
    public bool IsPhysical => false;
    public void Print(LabelData label) { }
}

/// <summary>
/// Normal Windows yazıcısı (ofis yazıcısı, "Microsoft Print to PDF"): etiketi sayfanın sol üstüne
/// GERÇEK BOYUTTA (50 × 30 mm) basar; kesip kumaşa yapıştırılabilir. Barkod gerçek Code128'dir.
/// Etiket yazıcısı (Zebra) için değildir: o, ham ZPL komutu alır (Adım 10).
/// </summary>
public sealed class PagePrinter(string queueName) : ILabelPrinter
{
    /// <summary>Sayfa kenarından boşluk (yazıcıların basamadığı kenar payı).</summary>
    private const double PageMarginMm = 15;

    public string QueueName { get; } = queueName;
    public string DisplayName => $"Kağıt: {QueueName}";
    public bool IsPhysical => true;

    public void Print(LabelData label)
    {
        // Yazdırma arayüz iş parçacığında olmalı (PrintService arka planda çağırır).
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new PrinterException("Uygulama kapanıyor; yazdırılamadı.");
        dispatcher.Invoke(() => PrintOnUiThread(label));
    }

    private void PrintOnUiThread(LabelData label)
    {
        try
        {
            var queue = FindQueue(QueueName)
                ?? throw new PrinterException($"'{QueueName}' yazıcısı bulunamadı. Ayarlar'dan yazıcıyı kontrol edin.");

            var dialog = new PrintDialog { PrintQueue = queue };
            var size = new Size(50 * LabelPreview.DipPerMm, 30 * LabelPreview.DipPerMm);
            var margin = PageMarginMm * LabelPreview.DipPerMm;

            var preview = new LabelPreview
            {
                Label = label, ShowEdge = true, SnapBarcodeToPixels = false,   // gerçek boyut, yazıcı çözünürlüğünde
                Width = size.Width, Height = size.Height,
            };
            var page = new Canvas { Width = size.Width + 2 * margin, Height = size.Height + 2 * margin, Background = Brushes.White };
            Canvas.SetLeft(preview, margin);
            Canvas.SetTop(preview, margin);
            page.Children.Add(preview);
            page.Measure(new Size(page.Width, page.Height));
            page.Arrange(new Rect(0, 0, page.Width, page.Height));
            page.UpdateLayout();

            dialog.PrintVisual(page, $"Kumaş etiketi {label.CutId}");
        }
        catch (PrinterException) { throw; }
        catch (Exception ex)
        {
            throw new PrinterException($"'{QueueName}' yazıcısına gönderilemedi: {ex.Message}", ex);
        }
    }

    /// <summary>Bu bilgisayarda kurulu yazıcıların adları.</summary>
    public static IReadOnlyList<string> InstalledPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .Select(q => q.FullName)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static PrintQueue? FindQueue(string name)
    {
        var server = new LocalPrintServer();
        return server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
            .FirstOrDefault(q => string.Equals(q.FullName, name, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Ayardaki yazıcı seçimine göre gerçek yazıcıya yönlendirir. Ayar değişince uygulamayı
/// yeniden başlatmak gerekmez. Ayar değeri: "screen" | "file" | "page:&lt;yazıcı adı&gt;".
/// </summary>
public sealed class SelectablePrinter : ILabelPrinter
{
    public const string ScreenSetting = "screen";
    public const string FileSetting = "file";
    public const string PagePrefix = "page:";

    private readonly SettingsRepository _settings;
    private readonly FileLabelPrinter _file;
    private readonly ScreenOnlyPrinter _screen = new();
    private ILabelPrinter _current;

    public SelectablePrinter(SettingsRepository settings, FileLabelPrinter file)
    {
        _settings = settings;
        _file = file;
        _current = Create(CurrentSetting);
    }

    /// <summary>Seçim değişti (durum çubuğu yazıcı adını yeniler).</summary>
    public event Action? Changed;

    public string CurrentSetting => _settings.Get(SettingKeys.Printer) ?? ScreenSetting;

    public string DisplayName => _current.DisplayName;
    public bool IsPhysical => _current.IsPhysical;

    public void Print(LabelData label) => _current.Print(label);

    public void Select(string setting)
    {
        _current = Create(setting);
        _settings.Set(SettingKeys.Printer, setting);
        Changed?.Invoke();
    }

    private ILabelPrinter Create(string setting) => setting switch
    {
        FileSetting => _file,
        _ when setting.StartsWith(PagePrefix, StringComparison.Ordinal) => new PagePrinter(setting[PagePrefix.Length..]),
        _ => _screen,
    };
}
