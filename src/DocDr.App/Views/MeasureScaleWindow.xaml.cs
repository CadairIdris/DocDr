using System.Windows;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class MeasureScaleWindow : Window
{
    public MeasureScaleWindow(MeasureScaleViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Close = Close;
        Loaded += (_, _) =>
        {
            System.Windows.Controls.TextBox box = viewModel.IsCalibration ? KnownBox : RatioBox;
            box.Focus();
            box.SelectAll();
        };
    }
}
