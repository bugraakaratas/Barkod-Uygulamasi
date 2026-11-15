using System.Windows.Controls;
using System.Windows.Input;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

public partial class PersonnelPage : Page
{
    private readonly PersonnelViewModel _vm;

    public PersonnelPage(PersonnelViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await viewModel.ActivateAsync();
            if (viewModel.NeedsLogin) LoginBadgeBox.Focus();
        };
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
