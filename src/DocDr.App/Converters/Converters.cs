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

/// <summary>
/// Maps the split-view flag to a <see cref="GridLength"/>: false collapses the column to 0;
/// true gives a star column, or <c>Auto</c> when the parameter is "auto" (used for the splitter).
/// </summary>
public sealed class SplitViewWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true)
        {
            return new GridLength(0);
        }

        return string.Equals(parameter as string, "auto", StringComparison.OrdinalIgnoreCase)
            ? GridLength.Auto
            : new GridLength(1, GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Page aspect (height/width) → thumbnail box height at the fixed thumbnail width.</summary>
public sealed class ThumbnailBoxHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double aspect = value is double d && d > 0 ? d : 1.294;
        return ViewModels.ThumbnailStripViewModel.ThumbnailWidth * aspect;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Highlight palette key (string) → an opaque swatch brush.</summary>
public sealed class HighlightSwatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(ViewModels.AnnotationColors.ToColor(value as string));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Enum value → Visible when it matches the parameter's member name, else Collapsed.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;

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
