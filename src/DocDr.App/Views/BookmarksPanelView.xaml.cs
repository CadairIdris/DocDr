using System.Windows;
using System.Windows.Controls;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class BookmarksPanelView : UserControl
{
    public BookmarksPanelView()
    {
        InitializeComponent();
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is BookmarksViewModel viewModel && e.NewValue is BookmarkNodeViewModel node)
        {
            viewModel.Activate(node);
        }
    }
}
