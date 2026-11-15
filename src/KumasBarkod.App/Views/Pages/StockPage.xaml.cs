using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

public partial class StockPage : Page
{
    private readonly StockViewModel _vm;

    public StockPage(StockViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Ekran görünürken sunucu 3 sn'de bir yoklanır; görünmezken yoklanmaz.
        Loaded += (_, _) =>
        {
            viewModel.Activate();
            SearchBox.Focus();
        };
        Unloaded += (_, _) => viewModel.Deactivate();

        // Kısayollar (üst denetim yutmasın diye handledEventsToo): Ctrl+F arama, Esc ayrıntıyı kapat / aramayı temizle.
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnKey), handledEventsToo: true);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (_vm.IsDetailOpen) _vm.CloseDetailCommand.Execute(null);
            else _vm.SearchText = "";
            e.Handled = true;
        }
    }

    /// <summary>Okutulan barkod (ScanCapture → MainWindow) bu ekranda açılır.</summary>
    public void OpenRoll(string cutId)
    {
        _vm.OpenRoll(cutId);
        Dispatcher.BeginInvoke(() => SearchBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }
}
