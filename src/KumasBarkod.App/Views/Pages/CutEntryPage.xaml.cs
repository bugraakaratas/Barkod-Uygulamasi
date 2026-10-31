using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

/// <summary>
/// Klavye akışı: Lot → Enter → Metre → Enter → Kaydet → odak tekrar Metre'de, lot aynı kalır.
/// Lot kutusunda ↑ ↓ son lotlar arasında gezer. Açılır liste kullanılmaz: odak hep tahmin edilebilir.
/// Odak yönetimi görünümün işi olduğu için burada; iş mantığı ViewModel'de.
/// </summary>
public partial class CutEntryPage : Page
{
    private readonly CutEntryViewModel _viewModel;

    public CutEntryPage(CutEntryViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.FocusRequested += Focus;
        Loaded += (_, _) =>
        {
            _viewModel.Load();
            Focus(string.IsNullOrWhiteSpace(_viewModel.LotText) ? EntryField.Lot : EntryField.Length);
        };
        PreviewKeyDown += Page_PreviewKeyDown;
    }

    private void Focus(EntryField field)
    {
        // Input önceliği: düzen ve bağlama güncellemeleri bittikten sonra odakla.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var box = field == EntryField.Lot ? LotBox : LengthBox;
            box.Focus();
            box.SelectAll();
        });
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            Focus(EntryField.Lot);
            e.Handled = true;
        }
    }

    private void LotBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.StepRecentLot(+1);
                LotBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.StepRecentLot(-1);
                LotBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Enter:
                Focus(EntryField.Length);
                e.Handled = true;
                break;
        }
    }

    private void RecentLotsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Seçim bağlama ile LotText'e yazılır; ardından metreye geç.
        Focus(EntryField.Length);
    }
}
