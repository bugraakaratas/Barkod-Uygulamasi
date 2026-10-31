using System.Windows.Controls;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            viewModel.Load();
            UrlBox.Focus();   // odak içerik çerçevesinde kalıp siyah odak kenarı göstermesin
        };
    }
}
