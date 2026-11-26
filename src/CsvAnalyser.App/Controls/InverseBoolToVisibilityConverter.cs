using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace CsvAnalyser.App.Controls;

/// <summary>True → Collapsed, false → Visible (e.g. a button hidden for the opened file, shown for the others).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v != Visibility.Visible;
}
