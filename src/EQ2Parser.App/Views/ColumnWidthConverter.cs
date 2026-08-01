using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EQ2Parser.App.Views;

/// <summary>bool (column visible) + width parameter → GridLength. Header and
/// row grids share it so a hidden column collapses in both at once.</summary>
public sealed class ColumnWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true
        && parameter is string text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            ? new GridLength(width)
            : new GridLength(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
