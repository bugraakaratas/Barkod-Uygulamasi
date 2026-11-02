using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

/// <summary>
/// Okutma ekranı. Odak hep okutma kutusundadır (iptal paneli açıkken panelde).
/// Kısayollar handledEventsToo ile dinlenir (üst denetimler F5'i yutuyor).
/// Tuşa bağlı odak değişimi anında yapılır; ertelenirse hızlı gelen karakter yanlış kutuya düşer.
/// </summary>
public partial class ScanPage : Page
{
    private readonly ScanViewModel _viewModel;

    public ScanPage(ScanViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        viewModel.FocusRequested += field =>
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusNow(field));
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            FocusNow(_viewModel.IsVoidPanelOpen ? ScanField.VoidReason : ScanField.Input));

        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Page_PreviewKeyDown), handledEventsToo: true);
    }

    private void FocusNow(ScanField field)
    {
        if (field == ScanField.VoidReason)
        {
            VoidPanel.Focus();
            // 1366×768'de panel (onay düğmeleri dahil) ekranın altında kalıyordu.
            VoidPanel.UpdateLayout();
            VoidPanel.BringIntoView();
            return;
        }
        ScanBox.Focus();
        ScanBox.SelectAll();
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // İptal paneli açıkken (ve okutma kutusuna tıklanmamışken): 1–4 / ↑↓ seçer, Enter onaylar.
        // Ctrl+1..4 ekran kısayoludur; okutmanın Enter'ı ScanCapture'da işlenmiş gelir (atla).
        if (_viewModel.IsVoidPanelOpen && !ScanBox.IsKeyboardFocusWithin
            && Keyboard.Modifiers == ModifierKeys.None && !e.Handled
            && HandleVoidPanelKey(e))
            return;

        switch (e.Key)
        {
            case Key.F5:
                Run(_viewModel.ReprintCommand);
                e.Handled = true;
                break;
            case Key.F8:
                Run(_viewModel.BeginVoidCommand);
                e.Handled = true;
                break;
            case Key.F9:
                Run(_viewModel.StartReplacementCommand);
                e.Handled = true;
                break;
            case Key.Escape:
                if (_viewModel.IsVoidPanelOpen)
                    _viewModel.CancelVoidCommand.Execute(null);
                else
                {
                    _viewModel.InputText = "";
                    FocusNow(ScanField.Input);
                }
                e.Handled = true;
                break;
        }
    }

    private void ScanBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Okuyucu ile gelen okutmayı pencere düzeyindeki ScanCapture yakalar (Enter "handled" olur,
        // buraya gelmez). Buraya gelen Enter, elle yazılmış ID'dir.
        if (e.Key != Key.Enter) return;
        _viewModel.SubmitCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>İptal paneli tuşları. true → tuş kullanıldı (diğer kısayollara gitmez).</summary>
    private bool HandleVoidPanelKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            // Rakamlar "handled" işaretlenmez: okuyucu panel açıkken okutursa karakterler
            // ScanCapture'a (TextInput) ulaşmalı. Odak metin kutusunda olmadığı için zararsız.
            case >= Key.D1 and <= Key.D9:
                _viewModel.SelectVoidReason(e.Key - Key.D0);
                return true;
            case >= Key.NumPad1 and <= Key.NumPad9:
                _viewModel.SelectVoidReason(e.Key - Key.NumPad0);
                return true;
            case Key.Up:
                _viewModel.MoveVoidReason(-1);
                e.Handled = true;
                return true;
            case Key.Down:
                _viewModel.MoveVoidReason(+1);
                e.Handled = true;
                return true;
            case Key.Enter:
                _viewModel.ConfirmVoidCommand.Execute(null);
                e.Handled = true;
                return true;
            default:
                return false;
        }
    }

    private static void Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }
}
