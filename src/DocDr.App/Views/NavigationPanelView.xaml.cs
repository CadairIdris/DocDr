using System.Windows;
using System.Windows.Controls;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class NavigationPanelView : UserControl
{
    public NavigationPanelView()
    {
        InitializeComponent();
    }

    // The IsChecked bindings are OneWay (they only reflect NavigationTab outward); the section
    // change is driven from the Checked event, mirroring the pane's view-mode selector.
    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is DocumentTabViewModel vm && sender is RadioButton { Tag: NavigationTab tab })
        {
            vm.NavigationTab = tab;
        }
    }
}
