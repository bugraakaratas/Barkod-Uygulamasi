using System.Windows.Controls;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

public partial class ScanPage : Page
{
    public ScanPage(ScanViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
