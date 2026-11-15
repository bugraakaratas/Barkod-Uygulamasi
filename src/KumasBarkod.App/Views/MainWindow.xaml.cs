using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Messaging;
using KumasBarkod.App.Scanning;
using KumasBarkod.App.ViewModels;
using KumasBarkod.App.Views.Pages;
using KumasBarkod.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.Views;

public partial class MainWindow : FluentWindow
{
    private readonly ScanCapture _scanCapture;
    private readonly SettingsRepository _settings;
    private readonly IServiceProvider _services;

    /// <summary>Göreve göre sol menüde görünen ekranlar (sırası = Ctrl+1, Ctrl+2, Ctrl+3). Ayarlar her zaman Ctrl+4.</summary>
    private IReadOnlyList<(NavigationViewItem Item, Type Page)> _menu = [];
    private string _role = StationRoles.Qc;

    public MainWindow(
        MainWindowViewModel viewModel,
        ScanViewModel scanViewModel,
        SettingsRepository settings,
        IServiceProvider services)
    {
        _settings = settings;
        _services = services;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Refresh();
        ApplyRole();

        // Sayfalar DI kapsayıcısından alınır; böylece ViewModel'ler enjekte edilir.
        RootNavigation.SetServiceProvider(services);
        Loaded += (_, _) => RootNavigation.Navigate(StartPage());

        // Görev Ayarlar'dan değişince menü hemen güncellenir.
        WeakReferenceMessenger.Default.Register<SettingsChangedMessage>(this, (_, _) => ApplyRole());

        // Hangi ekranda okutulursa okutulsun: kalite kontrolde Okutma ekranında yerel kayıt,
        // sevkiyat/yönetim bilgisayarında Depo Stoğu'nda sunucudaki topun geçmişi açılır.
        _scanCapture = new ScanCapture(this);
        WeakReferenceMessenger.Default.Register<OpenCutMessage>(this, (_, m) =>
        {
            if (_role == StationRoles.Qc)
            {
                RootNavigation.Navigate(typeof(ScanPage));
                scanViewModel.Open(m.CutId);
            }
            else
            {
                RootNavigation.Navigate(typeof(StockPage));
                _services.GetRequiredService<StockPage>().OpenRoll(m.CutId);
            }
        });

        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnShortcut), handledEventsToo: true);
    }

    private void OnShortcut(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        var index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            _ => -1,
        };
        if (index < 0) return;
        Type? page = index == 3 ? typeof(SettingsPage) : index < _menu.Count ? _menu[index].Page : null;
        if (page is null) return;
        RootNavigation.Navigate(page);
        e.Handled = true;
    }

    /// <summary>Görev: kalite kontrol (Kesim, Okutma, Geçmiş) · sevkiyat (Depo Stoğu) · yönetim (Depo Stoğu, Lotlar).</summary>
    private void ApplyRole()
    {
        var role = _settings.GetStationRole();
        var changed = role != _role || _menu.Count == 0;
        _role = role;

        var all = new (NavigationViewItem Item, Type Page)[]
        {
            (NavCutEntry, typeof(CutEntryPage)),
            (NavScan, typeof(ScanPage)),
            (NavHistory, typeof(HistoryPage)),
            (NavStock, typeof(StockPage)),
            (NavLots, typeof(LotsPage)),
        };
        var visible = role switch
        {
            StationRoles.Dock => new[] { typeof(StockPage) },
            StationRoles.Office => new[] { typeof(StockPage), typeof(LotsPage) },
            _ => new[] { typeof(CutEntryPage), typeof(ScanPage), typeof(HistoryPage) },
        };
        foreach (var (item, page) in all)
            item.Visibility = visible.Contains(page) ? Visibility.Visible : Visibility.Collapsed;
        _menu = visible.Select(p => all.First(a => a.Page == p)).ToList();
        for (var i = 0; i < _menu.Count; i++) _menu[i].Item.ToolTip = $"Ctrl+{i + 1}";

        (DataContext as MainWindowViewModel)?.Refresh();
        if (changed && IsLoaded && RootNavigation.SelectedItem is NavigationViewItem current
            && current.Visibility != Visibility.Visible)
            RootNavigation.Navigate(_menu[0].Page);
    }

    /// <summary>
    /// Açılış sayfası: görevin ilk ekranı. Yalnızca test/sunum için KUMASBARKOD_START_PAGE
    /// (scan | history | settings | stock | lots) başka sayfayla açar — arayüz denemesinde klavyeye tuş
    /// göndermeden ekran görüntüsü alınabilsin diye.
    /// </summary>
    private Type StartPage() => Environment.GetEnvironmentVariable("KUMASBARKOD_START_PAGE")?.Trim().ToLowerInvariant() switch
    {
        "scan" => typeof(ScanPage),
        "history" => typeof(HistoryPage),
        "settings" => typeof(SettingsPage),
        "stock" => typeof(StockPage),
        "lots" => typeof(LotsPage),
        _ => _menu[0].Page,
    };
}
