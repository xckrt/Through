using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Through.Tray;

/// <summary>Значение enum совпадает с параметром → true (для вкладок) или Visible (для страниц).</summary>
public sealed class EnumIs : IValueConverter
{
    public bool ToVisibility { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var match = value?.ToString() == parameter?.ToString();
        return ToVisibility ? match ? Visibility.Visible : Visibility.Collapsed : match;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Пустая коллекция → показать «здесь пока пусто».</summary>
public sealed class ZeroToVisible : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
