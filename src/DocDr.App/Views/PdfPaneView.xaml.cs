using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class PdfPaneView : UserControl
{
    private PdfPaneViewModel? _pane;
    private ScrollViewer? _scrollViewer;
    private bool _programmaticScroll;

    public PdfPaneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_pane is not null)
        {
            _pane.ScrollToPageRequested -= OnScrollToPageRequested;
        }

        _pane = e.NewValue as PdfPaneViewModel;

        if (_pane is null)
        {
            return;
        }

        _pane.ScrollToPageRequested += OnScrollToPageRequested;

        // WPF's TabControl reuses this single PdfPaneView across every tab, swapping the
        // DataContext underneath it — so a plain event hook isn't enough. Re-establish
        // viewport metrics, reset the scroll surface, and kick a fresh render for the new pane.
        _scrollViewer ??= FindScrollViewer(PageList);
        _programmaticScroll = false;
        _scrollViewer?.ScrollToVerticalOffset(0);
        ScheduleReinitialize();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scrollViewer = FindScrollViewer(PageList);
        ScheduleReinitialize();
    }

    private void ScheduleReinitialize()
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_pane is null)
                {
                    return;
                }

                _scrollViewer ??= FindScrollViewer(PageList);
                PushViewportMetrics();
                RefreshVisibleRange();

                if (_pane is { Mode: ViewMode.Continuous, CurrentPage: > 1 })
                {
                    OnScrollToPageRequested(_pane.CurrentPage - 1);
                }
            },
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void PushViewportMetrics()
    {
        if (_pane is null)
        {
            return;
        }

        double dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _pane.SetDeviceScale(dpiScale);

        if (_scrollViewer is not null && _scrollViewer.ViewportWidth > 0)
        {
            _pane.SetViewport(_scrollViewer.ViewportWidth, _scrollViewer.ViewportHeight);
        }
        else
        {
            _pane.SetViewport(ActualWidth, ActualHeight);
        }
    }

    private void PageList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _scrollViewer ??= FindScrollViewer(PageList);
        if (_pane is null || _scrollViewer is null)
        {
            return;
        }

        if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)
        {
            _pane.SetViewport(_scrollViewer.ViewportWidth, _scrollViewer.ViewportHeight);
        }

        RefreshVisibleRange();

        if (!_programmaticScroll && _pane.Mode == ViewMode.Continuous)
        {
            _pane.ReportScrolledToPage(_pane.GetPageAtOffset(_scrollViewer.VerticalOffset));
        }
    }

    private void RefreshVisibleRange()
    {
        if (_pane is null || _scrollViewer is null)
        {
            return;
        }

        if (_pane.Mode == ViewMode.SinglePage)
        {
            _pane.UpdateVisibleRange(_pane.CurrentPage - 1, _pane.CurrentPage - 1);
            return;
        }

        double top = _scrollViewer.VerticalOffset;
        double bottom = top + Math.Max(1, _scrollViewer.ViewportHeight);
        int first = _pane.GetPageAtOffset(top);
        int last = _pane.GetPageAtOffset(bottom);
        _pane.UpdateVisibleRange(first, last);
    }

    private void OnScrollToPageRequested(int pageIndex)
    {
        if (_scrollViewer is null || _pane is null || _pane.Mode == ViewMode.SinglePage)
        {
            return;
        }

        _programmaticScroll = true;
        _scrollViewer.ScrollToVerticalOffset(_pane.GetPageOffset(pageIndex));
        Dispatcher.BeginInvoke(() => _programmaticScroll = false,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
        {
            return sv;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            ScrollViewer? found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
