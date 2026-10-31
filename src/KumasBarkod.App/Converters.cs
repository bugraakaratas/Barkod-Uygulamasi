using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KumasBarkod.App;

/// <summary>0 → Visible, diğer → Collapsed ("Henüz kayıt yok" gibi boş durum metinleri için).</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
