using System.Windows;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class WatermarkReviewWindow : Window
{
    public WatermarkReviewWindow(WatermarkReviewViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.StartPreview();
    }
}
