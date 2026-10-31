using System.Windows.Controls;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

public partial class HistoryPage : Page
{
    public HistoryPage(HistoryViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
