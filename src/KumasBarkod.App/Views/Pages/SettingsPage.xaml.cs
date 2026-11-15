using System.Windows.Controls;
using System.Windows.Input;
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
            ServerBox.Focus();   // odak içerik çerçevesinde kalıp siyah odak kenarı göstermesin (ilk kart)
        };
        _vm = viewModel;
    }

    private readonly SettingsViewModel _vm;

    // Sunucu Sheets kartında yetkili girişi: kart kodu → Enter → PIN → Enter → giriş (okuyucu kartı okuyunca Enter gönderir).
    private void SheetsBadgeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SheetsPinBox.Focus();
        e.Handled = true;
    }

    private void SheetsPinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _vm.ServerSheets.LoginCommand.Execute(null);
        e.Handled = true;
    }
}
