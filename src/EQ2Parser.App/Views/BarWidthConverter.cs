using System.Globalization;
using System.Windows.Data;

namespace EQ2Parser.App.Views;

/// <summary>Meter bar sizing: fraction (0..1) × the row's available width.</summary>
public sealed class BarWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is [double fraction, double width] && width > 0)
            return Math.Max(0, Math.Min(1, fraction)) * width;
        return 0d;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
