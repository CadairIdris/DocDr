using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class ThumbnailStripView : UserControl
{
    // StackPanel: 118*avgAspect (~153) + label (~14) + margins (8). A rough uniform height is
    // fine here — it only drives which thumbnails get rendered, with a buffer either side.
    private const double ApproxItemHeight = 180.0;

    private ThumbnailStripViewModel? _vm;
    private ScrollViewer? _scrollViewer;

    public ThumbnailStripView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            _scrollViewer = FindScrollViewer(ThumbList);
            PushDeviceScale();
            RequestVisible();
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                PushDeviceScale();
                RequestVisible();
            }
        };
    }

    private bool _syncingSelection;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.CurrentPageChanged -= OnCurrentPageChanged;
        }

        _vm = e.NewValue as ThumbnailStripViewModel;

        if (_vm is not null)
        {
            _vm.CurrentPageChanged += OnCurrentPageChanged;
        }
    }

    private void OnCurrentPageChanged(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= ThumbList.Items.Count)
        {
            return;
        }

        Dispatcher.BeginInvoke(() => ThumbList.ScrollIntoView(ThumbList.Items[pageIndex]),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ThumbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || _syncingSelection)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            _vm.SetSelection(ThumbList.SelectedItems);
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void PushDeviceScale() =>
        _vm?.SetDeviceScale(VisualTreeHelper.GetDpi(this).DpiScaleX);

    private void ThumbList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _scrollViewer ??= FindScrollViewer(ThumbList);
        RequestVisible();
    }

    private void RequestVisible()
    {
        if (_vm is null || _scrollViewer is null || !IsVisible || ThumbList.Items.Count == 0)
        {
            return;
        }

        // Prefer WPF's realised containers — a fixed ApproxItemHeight (or even the panel's own
        // extent estimate) drifts tens of items off after a jump deep into a long strip, so the
        // wrong thumbnails get requested and the visible ones stay blank (same failure the page
        // pane's VisiblePageRange fixed).
        if (RealisedRange() is { } r)
        {
            _vm.RequestRange(r.First - 2, r.Last + 2);
            return;
        }

        double top = _scrollViewer.VerticalOffset;
        double viewport = System.Math.Max(1, _scrollViewer.ViewportHeight);
        double itemHeight = _scrollViewer.ExtentHeight > 0
            ? _scrollViewer.ExtentHeight / ThumbList.Items.Count
            : ApproxItemHeight;
        _vm.RequestRange((int)(top / itemHeight) - 2, (int)((top + viewport) / itemHeight) + 2);
    }

    /// <summary>The index span of thumbnail containers that actually intersect the viewport, or null
    /// if none are realised yet.</summary>
    private (int First, int Last)? RealisedRange()
    {
        double viewport = System.Math.Max(1, _scrollViewer!.ViewportHeight);
        int first = int.MaxValue, last = -1;

        for (int i = 0; i < ThumbList.Items.Count; i++)
        {
            if (ThumbList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement { IsVisible: true } c)
            {
                continue;
            }

            double y;
            try { y = c.TransformToVisual(_scrollViewer).Transform(new System.Windows.Point(0, 0)).Y; }
            catch (System.InvalidOperationException) { continue; }

            if (y + c.ActualHeight >= 0 && y <= viewport)
            {
                first = System.Math.Min(first, i);
                last = System.Math.Max(last, i);
            }
        }

        return last >= 0 ? (first, last) : null;
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
