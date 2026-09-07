using System.Windows;
using System.Windows.Controls;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class ClausesPanelView : UserControl
{
    public ClausesPanelView()
    {
        InitializeComponent();
    }

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ClausesViewModel viewModel && e.NewValue is ClauseNodeViewModel node)
        {
            viewModel.Activate(node);
        }
    }
}
