using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class BookmarksPanelView : UserControl
{
    public BookmarksPanelView()
    {
        InitializeComponent();
    }

    private BookmarksViewModel? ViewModel => DataContext as BookmarksViewModel;

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ViewModel is { } viewModel && e.NewValue is BookmarkNodeViewModel node)
        {
            viewModel.Activate(node);
        }
    }

    private void TreeViewItem_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
            e.Handled = true; // stop it reaching the parent item; the menu still opens on button-up
        }
    }

    private void RenameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is BookmarkNodeViewModel node)
        {
            node.IsEditing = true;
        }
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (Tree.SelectedItem is BookmarkNodeViewModel node)
        {
            ViewModel?.DeleteNodeCommand.Execute(node);
        }
    }

    private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Tree.SelectedItem is not BookmarkNodeViewModel node || node.IsEditing || ViewModel is null)
        {
            return;
        }

        if (e.Key == Key.F2)
        {
            node.IsEditing = true;
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            ViewModel.DeleteNodeCommand.Execute(node);
            e.Handled = true;
        }
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.IsVisible)
        {
            box.Tag = box.Text; // original, to detect a real change
            box.Focus();
            box.SelectAll();
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e) => Commit((TextBox)sender);

    private void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.Key == Key.Enter)
        {
            Commit(box);
            Tree.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (box.DataContext is BookmarkNodeViewModel node)
            {
                node.Title = box.Tag as string ?? node.Title;
                ViewModel?.CommitRename(node, changed: false);
            }

            Tree.Focus();
            e.Handled = true;
        }
    }

    private void Commit(TextBox box)
    {
        if (box.DataContext is BookmarkNodeViewModel node && node.IsEditing && ViewModel is { } viewModel)
        {
            viewModel.CommitRename(node, changed: (box.Tag as string) != node.Title);
        }
    }
}
