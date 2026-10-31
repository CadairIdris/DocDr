using System.Windows;
using System.Windows.Controls;

namespace DocDr.App.Views;

/// <summary>
/// The .NET 9 Fluent <see cref="TextBox"/> template always shows a "clear" ✕ (<c>ClearButton</c>)
/// while the box has focus and text, with no property to turn it off. In a multi-line box it's
/// vertically centred, so it drifts down as you type each new line — and a note already has Cancel
/// / Delete. <see cref="HideInMultilineBoxes"/> (called once from <c>App.OnStartup</c>) collapses it
/// in every <see cref="TextBox.AcceptsReturn"/> box app-wide; single-line boxes (find bar, page
/// number) keep the conventional clear button.
/// <para>
/// Done with a class-level <c>Loaded</c> handler rather than an implicit App-level style: a style
/// <c>BasedOn="{StaticResource {x:Type TextBox}}"</c> in App.xaml can resolve before the Fluent
/// theme is merged and pin every box to the classic look. A local <see cref="UIElement.Visibility"/>
/// outranks the template's trigger setters, so collapsing it once sticks through focus / text
/// changes; <c>Loaded</c> re-fires when a box is re-hosted, so a reused template is covered too.
/// </para>
/// </summary>
public static class TextBoxClearButton
{
    private const string PartName = "ClearButton";

    private static bool _registered;

    public static void HideInMultilineBoxes()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(
            typeof(TextBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { AcceptsReturn: true } box)
        {
            return;
        }

        box.ApplyTemplate();
        if (box.Template?.FindName(PartName, box) is UIElement clear)
        {
            clear.Visibility = Visibility.Collapsed;
        }
    }
}
