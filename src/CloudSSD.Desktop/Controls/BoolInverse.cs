using System.Globalization;
using System.Windows.Data;

namespace CloudSSD.Desktop.Controls;

public sealed class BoolInverse : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
