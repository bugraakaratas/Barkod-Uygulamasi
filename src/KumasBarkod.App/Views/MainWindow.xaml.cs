using KumasBarkod.App.ViewModels;
using KumasBarkod.App.Views.Pages;
using Wpf.Ui.Controls;

namespace KumasBarkod.App.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel, IServiceProvider services)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.Refresh();

        // Sayfalar DI kapsayıcısından alınır; böylece ViewModel'ler enjekte edilir.
        RootNavigation.SetServiceProvider(services);
        Loaded += (_, _) => RootNavigation.Navigate(typeof(CutEntryPage));
    }
}
