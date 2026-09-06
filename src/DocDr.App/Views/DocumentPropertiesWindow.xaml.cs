using System.Windows;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class DocumentPropertiesWindow : Window
{
    public DocumentPropertiesWindow(DocumentPropertiesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested = ok =>
        {
            DialogResult = ok;
            Close();
        };
    }
}
