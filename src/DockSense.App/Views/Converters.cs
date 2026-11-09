using System.Globalization;
using System.Windows.Data;

namespace DockSense.App.Views;

/// <summary>true → false and false → true, e.g. to disable a control for built-in items.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
