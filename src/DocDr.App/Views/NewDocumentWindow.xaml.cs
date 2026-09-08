using System.Windows;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class NewDocumentWindow : Window
{
    public NewDocumentWindow(NewDocumentViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
