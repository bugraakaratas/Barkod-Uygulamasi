using System.Windows.Controls;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

public partial class CutEntryPage : Page
{
    public CutEntryPage(CutEntryViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
