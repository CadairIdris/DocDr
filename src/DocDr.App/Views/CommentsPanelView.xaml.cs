using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class CommentsPanelView : UserControl
{
    public CommentsPanelView()
    {
        InitializeComponent();
    }

    /// <summary>A click on a card (not on its buttons or reply box) selects it and jumps the page
    /// to the note.</summary>
    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CommentCardViewModel card }
            || IsInsideInteractive(e.OriginalSource as DependencyObject, sender as DependencyObject))
        {
            return;
        }

        card.SelectCommand.Execute(null);
        e.Handled = true;
    }

    private static bool IsInsideInteractive(DependencyObject? node, DependencyObject? stopAt)
    {
        while (node is not null && !ReferenceEquals(node, stopAt))
        {
            if (node is ButtonBase or TextBoxBase)
            {
                return true;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>The card's ⋯ button opens its menu underneath it on a left click.</summary>
    private void CardMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>Ctrl+Enter posts the reply (plain Enter is a new line).</summary>
    private void ReplyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0
            && sender is FrameworkElement { DataContext: CommentCardViewModel card }
            && card.PostReplyCommand.CanExecute(null))
        {
            card.PostReplyCommand.Execute(null);
            e.Handled = true;
        }
    }
}
