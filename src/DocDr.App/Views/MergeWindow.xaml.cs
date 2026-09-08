using System.Windows;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class MergeWindow : Window
{
    public MergeWindow(MergeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
