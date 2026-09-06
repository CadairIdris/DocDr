using System.ComponentModel;
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
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // The right pane starts collapsed (split view is off by default); when it is first
        // shown it needs the same kick as a fresh DataContext.
        if (e.NewValue is true && _pane is not null)
        {
            ScheduleReinitialize();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_pane is not null)
        {
            _pane.ScrollToPageRequested -= OnScrollToPageRequested;
            _pane.PropertyChanged -= OnPanePropertyChanged;
            _pane.PagesReloaded -= OnPagesReloaded;
        }

        _pane = e.NewValue as PdfPaneViewModel;

        if (_pane is null)
        {
            return;
        }

        _pane.ScrollToPageRequested += OnScrollToPageRequested;
        _pane.PropertyChanged += OnPanePropertyChanged;
        _pane.PagesReloaded += OnPagesReloaded;

        // WPF's TabControl reuses this single PdfPaneView across every tab, swapping the
        // DataContext underneath it — so a plain event hook isn't enough. Re-establish
        // viewport metrics, the item source for the current mode, reset the scroll surface,
        // and kick a fresh render for the new pane.
        _scrollViewer ??= FindScrollViewer(PageList);
        _programmaticScroll = false;
        _scrollViewer?.ScrollToVerticalOffset(0);
        ApplyViewMode();
        ScheduleReinitialize();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scrollViewer = FindScrollViewer(PageList);
        ApplyViewMode();
        ScheduleReinitialize();
    }

    private void OnPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfPaneViewModel.Mode))
        {
            ApplyViewMode();
            _scrollViewer?.ScrollToVerticalOffset(0);
            ScheduleReinitialize();
        }
    }

    private void OnPagesReloaded()
    {
        ApplyViewMode();
        ScheduleReinitialize();
    }

    /// <summary>Point the list at the right collection + template for the pane's current mode.</summary>
    private void ApplyViewMode()
    {
        if (_pane is null)
        {
            return;
        }

        if (_pane.Mode == ViewMode.Grid)
        {
            PageList.ItemTemplate = (DataTemplate)Resources["PageRowTemplate"];
            PageList.ItemsSource = _pane.Rows;
        }
        else
        {
            PageList.ItemTemplate = (DataTemplate)Resources["PageListItemTemplate"];
            PageList.ItemsSource = _pane.PagesView;
        }
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

                if (_pane.CurrentPage > 1 && _pane.Mode != ViewMode.SinglePage)
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

        if (!_programmaticScroll && _pane.Mode != ViewMode.SinglePage)
        {
            _pane.ReportScrolledToPage(TopVisiblePageIndex());
        }
    }

    private int TopVisiblePageIndex()
    {
        double offset = _scrollViewer!.VerticalOffset;
        return _pane!.Mode == ViewMode.Grid
            ? _pane.GetRowAtOffset(offset) * Math.Max(1, _pane.GridColumns)
            : _pane.GetPageAtOffset(offset);
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

        if (_pane.Mode == ViewMode.Grid)
        {
            _pane.UpdateVisibleRows(_pane.GetRowAtOffset(top), _pane.GetRowAtOffset(bottom));
        }
        else
        {
            _pane.UpdateVisibleRange(_pane.GetPageAtOffset(top), _pane.GetPageAtOffset(bottom));
        }
    }

    private void OnScrollToPageRequested(int pageIndex)
    {
        if (_scrollViewer is null || _pane is null || _pane.Mode == ViewMode.SinglePage)
        {
            return;
        }

        double offset = _pane.Mode == ViewMode.Grid
            ? _pane.GetRowOffsetForPage(pageIndex)
            : _pane.GetPageOffset(pageIndex);

        _programmaticScroll = true;
        _scrollViewer.ScrollToVerticalOffset(offset);
        Dispatcher.BeginInvoke(() => _programmaticScroll = false,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void PageList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_pane is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return; // plain wheel keeps the ScrollViewer's default scrolling
        }

        e.Handled = true;
        _scrollViewer ??= FindScrollViewer(PageList);
        if (_scrollViewer is null)
        {
            _pane.ZoomByWheel(e.Delta);
            return;
        }

        // Anchor the zoom on the point under the cursor.
        Point cursor = e.GetPosition(_scrollViewer);
        double fracX = _scrollViewer.ExtentWidth > 0
            ? (_scrollViewer.HorizontalOffset + cursor.X) / _scrollViewer.ExtentWidth
            : 0;
        double fracY = _scrollViewer.ExtentHeight > 0
            ? (_scrollViewer.VerticalOffset + cursor.Y) / _scrollViewer.ExtentHeight
            : 0;

        _pane.ZoomByWheel(e.Delta);

        _programmaticScroll = true;
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_scrollViewer is not null)
                {
                    _scrollViewer.ScrollToHorizontalOffset((fracX * _scrollViewer.ExtentWidth) - cursor.X);
                    _scrollViewer.ScrollToVerticalOffset((fracY * _scrollViewer.ExtentHeight) - cursor.Y);
                }

                _programmaticScroll = false;
            },
            System.Windows.Threading.DispatcherPriority.Loaded);
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
