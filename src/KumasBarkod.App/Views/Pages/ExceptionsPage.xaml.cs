using System.Windows.Controls;
using System.Windows.Input;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

public partial class ExceptionsPage : Page
{
    private readonly ExceptionsViewModel _vm;

    public ExceptionsPage(ExceptionsViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await viewModel.ActivateAsync();
            if (viewModel.NeedsLogin) LoginBadgeBox.Focus();
        };
        Unloaded += (_, _) => viewModel.Deactivate();
    }

    private void LoginBadgeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        LoginPinBox.Focus();
        e.Handled = true;
    }

    private void LoginPinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.LoginCommand.Execute(null);
        e.Handled = true;
    }
}
