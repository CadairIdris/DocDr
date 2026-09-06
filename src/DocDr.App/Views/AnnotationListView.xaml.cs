using System.Windows.Controls;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class AnnotationListView : UserControl
{
    public AnnotationListView()
    {
        InitializeComponent();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is AnnotationListViewModel viewModel && List.SelectedItem is AnnotationRowViewModel row)
        {
            viewModel.Activate(row);
        }
    }
}
