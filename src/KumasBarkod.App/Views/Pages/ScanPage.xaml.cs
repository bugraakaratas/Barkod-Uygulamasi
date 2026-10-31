using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

/// <summary>
/// Okutma ekranı. Odak hep okutma kutusundadır (iptal sebebi yazılırken hariç).
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
        var box = field == ScanField.VoidReason ? VoidReasonBox : ScanBox;
        box.Focus();
        box.SelectAll();
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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

    private void VoidReasonBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _viewModel.ConfirmVoidCommand.Execute(null);
        e.Handled = true;
    }

    private static void Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }
}
