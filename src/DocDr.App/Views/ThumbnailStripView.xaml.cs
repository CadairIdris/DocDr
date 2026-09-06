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
        if (_vm is null || _scrollViewer is null || !IsVisible)
        {
            return;
        }

        double top = _scrollViewer.VerticalOffset;
        double viewport = System.Math.Max(1, _scrollViewer.ViewportHeight);
        int first = (int)(top / ApproxItemHeight) - 2;
        int last = (int)((top + viewport) / ApproxItemHeight) + 2;
        _vm.RequestRange(first, last);
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
