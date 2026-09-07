using System.Windows;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class TableExtractWindow : Window
{
    public TableExtractWindow(TableExtractViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Closed += Close;
    }
}
