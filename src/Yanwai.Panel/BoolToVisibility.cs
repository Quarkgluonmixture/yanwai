using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Yanwai.Panel;

public sealed class BoolToVisibility : IValueConverter
{
    public static BoolToVisibility Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
