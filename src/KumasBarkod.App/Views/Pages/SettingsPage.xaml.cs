using System.Windows.Controls;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Mesaj sayfanın başında: alttaki düğmelerin (yedek, geri kurma) sonucu ekran dışında kalmasın.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SettingsViewModel.MessageText) or nameof(SettingsViewModel.MessageIsOpen)
                && viewModel.MessageIsOpen)
                PageScroll.ScrollToTop();
        };

        Loaded += (_, _) =>
        {
            viewModel.Load();
            UrlBox.Focus();   // odak içerik çerçevesinde kalıp siyah odak kenarı göstermesin
        };
    }
}
