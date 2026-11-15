using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.App.Scanning;
using KumasBarkod.App.ViewModels;
using KumasBarkod.App.Views.Pages;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly ScanCapture _scanCapture;

    public MainWindow(
        MainWindowViewModel viewModel,
        ScanViewModel scanViewModel,
        IServiceProvider services)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Refresh();

        // Sayfalar DI kapsayıcısından alınır; böylece ViewModel'ler enjekte edilir.
        RootNavigation.SetServiceProvider(services);
        Loaded += (_, _) => RootNavigation.Navigate(StartPage());

        // Hangi ekranda okutulursa okutulsun (veya geçmişten açılırsa): okutma ekranına geç ve kaydı aç.
        _scanCapture = new ScanCapture(this);
        WeakReferenceMessenger.Default.Register<OpenCutMessage>(this, (_, m) =>
        {
            RootNavigation.Navigate(typeof(ScanPage));
            scanViewModel.Open(m.CutId);
        });

        // Ekran kısayolları: Ctrl+1 Kesim Girişi, Ctrl+2 Okutma, Ctrl+3 Geçmiş, Ctrl+4 Ayarlar
        AddHandler(PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler((_, e) =>
        {
            if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
            var page = e.Key switch
            {
                System.Windows.Input.Key.D1 or System.Windows.Input.Key.NumPad1 => typeof(CutEntryPage),
                System.Windows.Input.Key.D2 or System.Windows.Input.Key.NumPad2 => typeof(ScanPage),
                System.Windows.Input.Key.D3 or System.Windows.Input.Key.NumPad3 => typeof(HistoryPage),
                System.Windows.Input.Key.D4 or System.Windows.Input.Key.NumPad4 => typeof(SettingsPage),
                _ => null,
            };
            if (page is null) return;
            RootNavigation.Navigate(page);
            e.Handled = true;
        }), handledEventsToo: true);
    }

    /// <summary>
    /// Açılış sayfası: her zaman Kesim Girişi. Yalnızca test/sunum için KUMASBARKOD_START_PAGE
    /// (scan | history | settings) başka sayfayla açar — arayüz denemesinde klavyeye tuş göndermeden
    /// ekran görüntüsü alınabilsin diye.
    /// </summary>
    private static Type StartPage() => Environment.GetEnvironmentVariable("KUMASBARKOD_START_PAGE")?.Trim().ToLowerInvariant() switch
    {
        "scan" => typeof(ScanPage),
        "history" => typeof(HistoryPage),
        "settings" => typeof(SettingsPage),
        _ => typeof(CutEntryPage),
    };
}
