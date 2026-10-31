using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

/// <summary>
/// Geçmiş. Kısayollar: Ctrl+F arama, Enter/çift tık kaydı aç, F5 tekrar bas,
/// Ctrl+PageUp/PageDown sayfa, aramada Esc temizle, aramada ↓ listeye geç.
/// </summary>
public partial class HistoryPage : Page
{
    private readonly HistoryViewModel _viewModel;

    public HistoryPage(HistoryViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            _viewModel.IsActive = true;
            SearchBox.Focus();
            await _viewModel.RefreshAsync();
        };
        Unloaded += (_, _) => _viewModel.IsActive = false;

        // Liste yenilenince (ör. tekrar baskı sonrası) odak listedeyse seçili satıra geri dön.
        var restoreFocus = false;
        _viewModel.RowsReloading += () => restoreFocus = Grid.IsKeyboardFocusWithin;
        _viewModel.RowsReloaded += () =>
        {
            if (!restoreFocus || Grid.SelectedIndex < 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusGridRow(Grid.SelectedIndex));
        };

        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Page_PreviewKeyDown), handledEventsToo: true);
    }

    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.F when ctrl:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.F5:
                Run(_viewModel.ReprintCommand);
                e.Handled = true;
                break;
            case Key.PageDown when ctrl:
                Run(_viewModel.NextPageCommand);
                e.Handled = true;
                break;
            case Key.PageUp when ctrl:
                Run(_viewModel.PreviousPageCommand);
                e.Handled = true;
                break;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _viewModel.SearchText = "";
                e.Handled = true;
                break;
            case Key.Down or Key.Enter when Grid.Items.Count > 0:
                FocusGridRow(Math.Max(0, Grid.SelectedIndex));
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Satırın ilk hücresine klavye odağı ver. (Satırın kendisinde MoveFocus(Next) kullanılırsa
    /// odak satırı atlayıp sol menüye gidiyor — testte yaşandı.)
    /// </summary>
    private void FocusGridRow(int index)
    {
        Grid.SelectedIndex = index;
        Grid.ScrollIntoView(Grid.Items[index]);
        Grid.UpdateLayout();
        Grid.CurrentCell = new DataGridCellInfo(Grid.Items[index], Grid.Columns[0]);

        if (Grid.ItemContainerGenerator.ContainerFromIndex(index) is DataGridRow row
            && FindChild<DataGridCellsPresenter>(row) is { } presenter
            && presenter.ItemContainerGenerator.ContainerFromIndex(0) is DataGridCell cell)
        {
            cell.Focus();
        }
        else
        {
            Grid.Focus();
        }
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Run(_viewModel.OpenSelectedCommand);
        e.Handled = true;   // DataGrid'in "sonraki satıra geç" davranışını engelle
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Run(_viewModel.OpenSelectedCommand);

    private static void Run(ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }
}
