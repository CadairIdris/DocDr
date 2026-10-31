using System.Windows;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class ExtractPagesWindow : Window
{
    public ExtractPagesWindow(ExtractPagesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Close = Close;
        Loaded += (_, _) =>
        {
            RangeBox.Focus();
            RangeBox.SelectAll();
        };
    }
}
