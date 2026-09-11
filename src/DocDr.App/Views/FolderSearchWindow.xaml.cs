using System.Windows;
using System.Windows.Controls;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class FolderSearchWindow : Window
{
    public FolderSearchWindow(FolderSearchViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void ResultsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is FolderSearchViewModel viewModel && e.NewValue is FolderSearchHitViewModel hit)
        {
            viewModel.Activate(hit);
        }
    }
}
