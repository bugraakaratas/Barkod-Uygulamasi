using System.ComponentModel;
using System.Windows.Controls;
using KumasBarkod.App.ViewModels;

namespace KumasBarkod.App.Views.Pages;

public partial class ReportsPage : Page
{
    public ReportsPage(ReportsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += async (_, _) => await viewModel.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>Sütun başlığı satır sınıfındaki [DisplayName]'den (Türkçe).</summary>
    private void Grid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyDescriptor is PropertyDescriptor p && !string.IsNullOrEmpty(p.DisplayName)) e.Column.Header = p.DisplayName;
        e.Column.Width = e.PropertyName is "Lot" or "Lots" or "Note" ? new DataGridLength(1, DataGridLengthUnitType.Star) : DataGridLength.Auto;
    }
}
