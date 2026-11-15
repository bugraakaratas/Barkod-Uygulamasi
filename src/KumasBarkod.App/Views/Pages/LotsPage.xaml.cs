using System.Windows.Controls;
using System.Windows.Input;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

public partial class LotsPage : Page
{
    private readonly LotsViewModel _vm;

    public LotsPage(LotsViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            viewModel.Activate();
            NewLotBox.Focus();
        };
        Unloaded += (_, _) => viewModel.Deactivate();
    }

    /// <summary>Yeni lot kutusunda Enter = Lot ekle.</summary>
    private void NewLotBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_vm.CreateCommand.CanExecute(null)) return;
        _vm.CreateCommand.Execute(null);
        e.Handled = true;
    }
}
