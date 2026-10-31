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
        CutEntryViewModel cutEntryViewModel,
        IServiceProvider services)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Refresh();

        // Sayfalar DI kapsayıcısından alınır; böylece ViewModel'ler enjekte edilir.
        RootNavigation.SetServiceProvider(services);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(CutEntryPage));

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

        // İptal edilen kaydın yerine yeni kayıt: giriş ekranı aynı lotla açılır.
        WeakReferenceMessenger.Default.Register<StartNewCutMessage>(this, (_, m) =>
        {
            cutEntryViewModel.PrepareNewCut(m.LotCode);
            RootNavigation.Navigate(typeof(CutEntryPage));
        });
    }
}
