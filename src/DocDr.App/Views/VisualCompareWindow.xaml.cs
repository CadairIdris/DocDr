using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class VisualCompareWindow : Window
{
    private const double ImageMargin = 16;
    private bool _fitted;

    public VisualCompareWindow(VisualCompareViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        viewModel.ImageChanged += OnImageChanged;
        viewModel.ScrollToRequested += OnScrollToRequested;
        Closed += (_, _) =>
        {
            viewModel.ImageChanged -= OnImageChanged;
            viewModel.ScrollToRequested -= OnScrollToRequested;
        };
    }

    public VisualCompareViewModel ViewModel { get; }

    private void OnImageChanged()
    {
        if (!_fitted && ViewModel.ImageWidth > 0)
        {
            _fitted = true;
            Dispatcher.BeginInvoke(FitWidth, DispatcherPriority.Loaded);
        }
    }

    private void FitWidth()
    {
        if (ViewModel.ImageWidth > 0 && Scroller.ViewportWidth > 0)
        {
            ViewModel.Zoom = Math.Clamp((Scroller.ViewportWidth - (2 * ImageMargin)) / ViewModel.ImageWidth, 0.1, 6);
        }
    }

    private void FitWidth_Click(object sender, RoutedEventArgs e) => FitWidth();

    private void FitPage_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ImageWidth > 0 && Scroller.ViewportWidth > 0 && Scroller.ViewportHeight > 0)
        {
            double zx = (Scroller.ViewportWidth - (2 * ImageMargin)) / ViewModel.ImageWidth;
            double zy = (Scroller.ViewportHeight - (2 * ImageMargin)) / ViewModel.ImageHeight;
            ViewModel.Zoom = Math.Clamp(Math.Min(zx, zy), 0.1, 6);
        }
    }

    /// <summary>Ctrl+wheel zooms about the pointer; a plain wheel scrolls.</summary>
    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        Point pointer = e.GetPosition(Scroller);
        Point onImage = e.GetPosition(ImageHost); // unscaled image DIPs
        double zoom = Math.Clamp(ViewModel.Zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2), 0.1, 6);
        ViewModel.Zoom = zoom;
        Scroller.UpdateLayout();
        Point origin = ImageHost.TranslatePoint(new Point(0, 0), Scroller);
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset + origin.X + (onImage.X * zoom) - pointer.X);
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + origin.Y + (onImage.Y * zoom) - pointer.Y);
    }

    /// <summary>Centre a box (image DIPs at 100 %) in the viewport.</summary>
    private void OnScrollToRequested(double x, double y, double width, double height) =>
        Dispatcher.BeginInvoke(() =>
        {
            Point origin = ImageHost.TranslatePoint(new Point(0, 0), Scroller);
            double zoom = ViewModel.Zoom;
            double cx = origin.X + Scroller.HorizontalOffset + ((x + (width / 2)) * zoom);
            double cy = origin.Y + Scroller.VerticalOffset + ((y + (height / 2)) * zoom);
            Scroller.ScrollToHorizontalOffset(cx - (Scroller.ViewportWidth / 2));
            Scroller.ScrollToVerticalOffset(cy - (Scroller.ViewportHeight / 2));
        }, DispatcherPriority.Loaded);

    private void Region_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CompareRegionViewModel region })
        {
            ViewModel.SelectedRegion = region;
            e.Handled = true;
        }
    }
}
