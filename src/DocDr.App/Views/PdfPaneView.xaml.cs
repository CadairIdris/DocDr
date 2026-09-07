using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Views;

public partial class PdfPaneView : UserControl
{
    private PdfPaneViewModel? _pane;
    private ScrollViewer? _scrollViewer;
    private bool _programmaticScroll;

    private bool _selecting;
    private bool _inking;
    private FrameworkElement? _selectionSlot;
    private int _selectionPageIndex = -1;
    private int _wheelAccumulator;

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

        // The two-page spread panel isn't an IScrollInfo, so item-based scrolling would make the
        // ScrollViewer report its viewport in "items" and the fit-to-screen maths collapse.
        ScrollViewer.SetCanContentScroll(PageList, _pane.Mode != ViewMode.TwoPage);

        if (_pane.Mode == ViewMode.Grid)
        {
            PageList.ItemsPanel = (ItemsPanelTemplate)Resources["VerticalPagesPanel"];
            PageList.ItemTemplate = (DataTemplate)Resources["PageRowTemplate"];
            PageList.ItemsSource = _pane.Rows;
        }
        else
        {
            PageList.ItemsPanel = (ItemsPanelTemplate)Resources[
                _pane.Mode == ViewMode.TwoPage ? "SpreadPanel" : "VerticalPagesPanel"];
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

                if (_pane.CurrentPage > 1 && !_pane.IsPaged)
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

        if (!_programmaticScroll && !_pane.IsPaged)
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

        if (_pane.Mode == ViewMode.TwoPage)
        {
            _pane.UpdateVisibleRange(_pane.SpreadLeftIndex, _pane.SpreadLeftIndex + 1);
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
        if (_scrollViewer is null || _pane is null || _pane.IsPaged)
        {
            return;
        }

        object? item = PageItem(pageIndex);
        if (item is null)
        {
            return;
        }

        // A plain ScrollToVerticalOffset(GetPageOffset(N)) drifts — the panel virtualises and
        // DPI-snaps each realised page a hair taller than our layout estimate, so the error
        // compounds and a jump deep into a long document lands a page or more short.
        // ScrollIntoView realises the target container; then top-align it from its real
        // on-screen position, refining as neighbours realise.
        _programmaticScroll = true;
        PageList.ScrollIntoView(item);
        AlignPageToTop(pageIndex, attempts: 4);
    }

    private void AlignPageToTop(int pageIndex, int attempts)
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                if (_pane is null || _scrollViewer is null)
                {
                    _programmaticScroll = false;
                    return;
                }

                FrameworkElement? container = PageList.ItemContainerGenerator
                    .ContainerFromItem(PageItem(pageIndex)) as FrameworkElement;

                double top = container is null
                    ? double.NaN
                    : container.TransformToVisual(_scrollViewer).Transform(new Point(0, 0)).Y;

                bool aligned = !double.IsNaN(top) && Math.Abs(top) <= 1.0;
                if (!aligned && attempts > 0)
                {
                    if (!double.IsNaN(top))
                    {
                        _scrollViewer.ScrollToVerticalOffset(Math.Max(0, _scrollViewer.VerticalOffset + top));
                    }
                    else
                    {
                        PageList.ScrollIntoView(PageItem(pageIndex));
                    }

                    AlignPageToTop(pageIndex, attempts - 1);
                    return;
                }

                Dispatcher.BeginInvoke(() => _programmaticScroll = false,
                    System.Windows.Threading.DispatcherPriority.Background);
            },
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private object? PageItem(int pageIndex)
    {
        if (_pane is null)
        {
            return null;
        }

        if (_pane.Mode == ViewMode.Grid)
        {
            int columns = Math.Max(1, _pane.GridColumns);
            int row = pageIndex / columns;
            return row >= 0 && row < _pane.Rows.Count ? _pane.Rows[row] : null;
        }

        return pageIndex >= 0 && pageIndex < _pane.Pages.Count ? _pane.Pages[pageIndex] : null;
    }

    private void PageList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_pane is null)
        {
            return;
        }

        // Read mode: a spread fills the screen, so the wheel turns pages instead of scrolling.
        if (_pane.Mode == ViewMode.TwoPage && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            e.Handled = true;
            _wheelAccumulator += e.Delta;
            if (Math.Abs(_wheelAccumulator) >= 120)
            {
                _pane.Advance(_wheelAccumulator < 0 ? 1 : -1);
                _wheelAccumulator = 0;
            }

            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
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

    private void ModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        // A RadioButton's Command doesn't fire reliably when IsChecked is also bound, and the
        // EnumEquals OneWay binding only reflects Mode outward — so drive Mode from Checked here.
        if (_pane is not null && sender is RadioButton { Tag: ViewMode mode })
        {
            _pane.Mode = mode;
        }
    }

    // --- Annotation text selection ---------------------------------------------------------

    private void PageList_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_pane is null || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            return;
        }

        // Let the note markers / popup handle their own clicks.
        if (e.OriginalSource is DependencyObject src && FindAncestor<ButtonBase>(src) is not null)
        {
            return;
        }

        (FrameworkElement Element, PageSlotViewModel Slot)? hit = FindSlot(e.OriginalSource as DependencyObject);
        if (hit is not { } target)
        {
            return;
        }

        Point local = e.GetPosition(target.Element);
        PdfPoint pagePoint = _pane.DevicePointToPage(target.Slot.PageIndex, local.X, local.Y);

        if (_pane.CommentToolActive)
        {
            _pane.AddCommentAt(target.Slot.PageIndex, pagePoint);
            e.Handled = true;
            return;
        }

        if (_pane.HighlighterToolActive)
        {
            _inking = true;
            _selectionSlot = target.Element;
            _selectionPageIndex = target.Slot.PageIndex;
            _pane.SelectedAnnotationId = null;
            _pane.BeginInk(target.Slot.PageIndex, pagePoint);
            PageList.CaptureMouse();
            e.Handled = true;
            return;
        }

        // A click on an existing highlight selects it (and its delete button) instead of
        // starting a new text selection.
        if (_pane.TrySelectAnnotationAt(target.Slot.PageIndex, pagePoint))
        {
            SelectionPopup.IsOpen = false;
            PageList.Focus();
            e.Handled = true;
            return;
        }

        SelectionPopup.IsOpen = false;
        _selecting = true;
        _selectionSlot = target.Element;
        _selectionPageIndex = target.Slot.PageIndex;
        _pane.BeginTextSelection(target.Slot.PageIndex, pagePoint);
        PageList.CaptureMouse();
        e.Handled = true;
    }

    private void PageList_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pane is null || _selectionSlot is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point local = e.GetPosition(_selectionSlot);
        PdfPoint pagePoint = _pane.DevicePointToPage(_selectionPageIndex, local.X, local.Y);

        if (_inking)
        {
            _pane.ExtendInk(pagePoint);
        }
        else if (_selecting)
        {
            _pane.ExtendTextSelection(pagePoint);
        }
    }

    private void PageList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pane is null)
        {
            return;
        }

        if (_inking)
        {
            _inking = false;
            PageList.ReleaseMouseCapture();
            _pane.EndInk();
            return;
        }

        if (!_selecting)
        {
            return;
        }

        _selecting = false;
        PageList.ReleaseMouseCapture();

        if (_pane.EndTextSelection())
        {
            SelectionPopup.IsOpen = true;
        }
    }

    private void PageList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.Delete || e.Key == Key.Back)
            && _pane?.SelectedAnnotationId is System.Guid id
            && _pane.DeleteAnnotationCommand.CanExecute(id))
        {
            _pane.DeleteAnnotationCommand.Execute(id);
            e.Handled = true;
        }
    }

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string key } && _pane?.CreateHighlightCommand.CanExecute(key) == true)
        {
            _pane.CreateHighlightCommand.Execute(key);
        }

        SelectionPopup.IsOpen = false;
    }

    private void PopupAction_Click(object sender, RoutedEventArgs e) => SelectionPopup.IsOpen = false;

    private (FrameworkElement Element, PageSlotViewModel Slot)? FindSlot(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Grid { DataContext: PageSlotViewModel slot } grid)
            {
                return (grid, slot);
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
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
