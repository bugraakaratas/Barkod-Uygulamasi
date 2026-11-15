using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

/// <summary>
/// Sevkiyat ekranı. Klavye: giriş (kart → Enter → PIN → Enter), listede Enter = başlat/devam,
/// okutmada F7 çıkarma modu, F9 onay, F8 iptal, Esc paneli kapat / çıkarma modunu kapat.
/// Odak okutmada hep okutma kutusunda: okuyucu kutuya yazar, Enter gönderir.
/// </summary>
public partial class ShipmentPage : Page
{
    private readonly ShipmentViewModel _vm;

    public ShipmentPage(ShipmentViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.FocusRequested += screen => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FocusFor(screen));
        Loaded += async (_, _) => await viewModel.ActivateAsync();
        Unloaded += (_, _) => viewModel.Deactivate();
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnKey), handledEventsToo: true);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShipmentViewModel.IsConfirmOpen) && viewModel.IsConfirmOpen)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ConfirmPanel.Focus());
            if (e.PropertyName == nameof(ShipmentViewModel.IsCancelOpen) && viewModel.IsCancelOpen)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => CancelPanel.Focus());
        };
    }

    /// <summary>Okuyucu ile okutulan barkod (ScanCapture → MainWindow). Okutma ekranındaysa işlenir.</summary>
    public bool TryHandleScan(string code)
    {
        if (!_vm.IsScanScreen) return false;
        _vm.SubmitScan(code);
        FocusFor(ShipmentScreen.Scanning);
        return true;
    }

    private void FocusFor(ShipmentScreen screen)
    {
        switch (screen)
        {
            case ShipmentScreen.Login:
                (string.IsNullOrEmpty(_vm.LoginBadge) ? (Control)BadgeBox : PinBox).Focus();
                break;
            case ShipmentScreen.List:
                if (LotList.Items.Count > 0)
                {
                    if (LotList.SelectedIndex < 0) LotList.SelectedIndex = 0;
                    (LotList.ItemContainerGenerator.ContainerFromIndex(LotList.SelectedIndex) as ListBoxItem)?.Focus();
                }
                break;
            case ShipmentScreen.Scanning:
                if (!_vm.IsConfirmOpen && !_vm.IsCancelOpen) ScanBox.Focus();
                break;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_vm.IsScanScreen || Keyboard.Modifiers != ModifierKeys.None) return;

        if (_vm.IsConfirmOpen && ConfirmPanel.IsKeyboardFocusWithin)
        {
            if (e.Key == Key.Enter && !e.Handled) { _vm.ConfirmCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Escape) { _vm.CloseConfirmPanel(); e.Handled = true; }
            return;
        }
        if (_vm.IsCancelOpen && CancelPanel.IsKeyboardFocusWithin)
        {
            switch (e.Key)
            {
                case Key.Enter when !e.Handled: _vm.CancelShipmentCommand.Execute(null); e.Handled = true; return;
                case Key.Escape: _vm.CloseCancelPanel(); e.Handled = true; return;
                // Rakamlar "işlendi" sayılmaz: panel açıkken yapılan okutma ScanCapture'a ulaşsın.
                case Key.D1 or Key.NumPad1 when !e.Handled: _vm.SelectCancelReason(0); return;
                case Key.D2 or Key.NumPad2 when !e.Handled: _vm.SelectCancelReason(1); return;
                case Key.D3 or Key.NumPad3 when !e.Handled: _vm.SelectCancelReason(2); return;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.F7: if (!e.IsRepeat) _vm.ToggleRemoveModeCommand.Execute(null); e.Handled = true; break;
            case Key.F9: if (!e.IsRepeat) _vm.BeginConfirmCommand.Execute(null); e.Handled = true; break;
            case Key.F8: if (!e.IsRepeat) _vm.BeginCancelCommand.Execute(null); e.Handled = true; break;
            case Key.Escape:
                if (_vm.IsRemoveMode) _vm.ToggleRemoveModeCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void ScanBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.SubmitScan(_vm.ScanText);
        e.Handled = true;
    }

    private void BadgeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        PinBox.Focus();
        e.Handled = true;
    }

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_vm.LoginCommand.CanExecute(null)) return;
        _vm.LoginCommand.Execute(null);
        e.Handled = true;
    }

    private void LotList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.StartCommand.CanExecute(null)) { _vm.StartCommand.Execute(null); e.Handled = true; }
    }

    private void LotList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.StartCommand.CanExecute(null)) _vm.StartCommand.Execute(null);
    }

    private void OpenList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.ResumeCommand.CanExecute(null)) { _vm.ResumeCommand.Execute(null); e.Handled = true; }
    }

    private void OpenList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.ResumeCommand.CanExecute(null)) _vm.ResumeCommand.Execute(null);
    }
}
