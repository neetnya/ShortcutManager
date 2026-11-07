using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ShortcutManager;

/// <summary>true → Visible，false → Collapsed。（在 App.xaml 里注册为全局资源）</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
