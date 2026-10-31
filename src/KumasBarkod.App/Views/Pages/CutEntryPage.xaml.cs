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
        // handledEventsToo: üstteki pencere/gezinme denetimi F5'i bize ulaşmadan "işlendi" olarak
        // işaretliyor; normal kayıtlı işleyici bu olayı hiç görmez.
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Page_PreviewKeyDown), handledEventsToo: true);
    }

    /// <summary>
    /// Tuşa basılınca odak HEMEN taşınır: ertelenirse hızlı yazılan (veya okuyucunun gönderdiği)
    /// sonraki karakter eski kutuya düşer. (Bir kez yaşandı: "LOT-A" + Enter + "25,5" → lot "LOT-A2".)
    /// </summary>
    private void FocusNow(EntryField field)
    {
        var box = field == EntryField.Lot ? LotBox : LengthBox;
        box.Focus();
        box.SelectAll();
    }

    /// <summary>
    /// ViewModel isteği (kayıt sonrası, sayfa açılışı): bağlama ve düzen güncellemeleri bitsin diye ertelenir.
    /// Bu anda odak zaten doğru kutuda olduğu için ara karakter kaybı olmaz.
    /// </summary>
    private void Focus(EntryField field) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusNow(field));

    /// <summary>
    /// Sayfa kısayolları burada ele alınır, KeyBinding ile değil: F5 üst denetimlerde yutuluyor.
    /// </summary>
    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F2:
                FocusNow(EntryField.Lot);
                e.Handled = true;
                break;
            case Key.F5:
                if (_viewModel.ReprintLastCommand.CanExecute(null))
                    _viewModel.ReprintLastCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                _viewModel.ClearLengthCommand.Execute(null);
                e.Handled = true;
                break;
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
                FocusNow(EntryField.Length);
                e.Handled = true;
                break;
        }
    }

    private void RecentLotsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Seçim bağlama ile LotText'e yazılır; ardından metreye geç.
        FocusNow(EntryField.Length);
    }
}
