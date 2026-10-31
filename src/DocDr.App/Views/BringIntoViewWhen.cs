using System.Windows;
using System.Windows.Threading;

namespace DocDr.App.Views;

/// <summary>
/// <c>views:BringIntoViewWhen.Value="{Binding IsSelected}"</c> — scrolls the element into view in
/// its enclosing <see cref="System.Windows.Controls.ScrollViewer"/> whenever the bound value turns
/// true. The Comments panel uses it so clicking a note on the page reveals its card.
/// </summary>
public static class BringIntoViewWhen
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(bool), typeof(BringIntoViewWhen), new PropertyMetadata(false, OnValueChanged));

    public static bool GetValue(DependencyObject element) => (bool)element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, bool value) => element.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && d is FrameworkElement element)
        {
            // After layout: a card that was just (re)created or whose group just expanded has no
            // size yet.
            element.Dispatcher.BeginInvoke(() => element.BringIntoView(), DispatcherPriority.Loaded);
        }
    }
}
