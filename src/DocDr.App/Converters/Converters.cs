using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DocDr.App.ViewModels;

namespace DocDr.App.Converters;

/// <summary>true → Collapsed, false → Visible. For "show this when the flag is off" cases.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>Picks the highlight fill brush depending on whether the rectangle is the active match.</summary>
public sealed class HighlightFillConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool active = value is true;
        string key = active ? "ActiveHighlightBrush" : "HighlightBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Yellow;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Enum equality test usable as a converter (parameter = enum member name).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null
            ? Enum.Parse(targetType, parameter.ToString()!)
            : Binding.DoNothing;
}
