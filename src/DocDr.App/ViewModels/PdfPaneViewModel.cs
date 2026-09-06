using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>
/// One viewer pane. Owns <em>all</em> per-view state — view mode, current page, zoom, and its
/// own independent text search — so the two panes never affect each other. Both panes share a
/// single <see cref="PdfDocument"/>; every PDFium call is serialised inside that type.
/// </summary>
public sealed partial class PdfPaneViewModel : ObservableObject, IDisposable
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.2;
    private const double FitMargin = 24.0;
    private const int MaxGridColumns = 6;

    private readonly PdfDocument _document;
    private readonly BackgroundRenderQueue _queue;
    private readonly PdfSearch _search;
    private readonly IReadOnlyList<PdfSize> _pageSizes;

    private CancellationTokenSource? _searchCts;
    private IReadOnlyList<SearchHit> _hits = [];
    private int _activeHit = -1;
    private double _deviceScale = 1.0;
    private double _viewportWidth;
    private double _viewportHeight;
    private bool _suppressScrollSync;

    public PdfPaneViewModel(string title, PdfDocument document, IReadOnlyList<PdfSize> pageSizes, BackgroundRenderQueue queue)
    {
        Title = title;
        _document = document;
        _pageSizes = pageSizes;
        _queue = queue;
        _search = new PdfSearch(document);

        Pages = new ObservableCollection<PageSlotViewModel>(
            pageSizes.Select((size, index) => new PageSlotViewModel(index, size)));
        Rows = [];

        PagesView = CollectionViewSource.GetDefaultView(Pages);
        PagesView.Filter = o => Mode != ViewMode.SinglePage
                                || (o is PageSlotViewModel slot && slot.PageIndex == CurrentPage - 1);

        _currentPage = PageCount > 0 ? 1 : 0;
        _zoom = 1.0;
        ApplyLayout();
    }

    public string Title { get; }

    public ObservableCollection<PageSlotViewModel> Pages { get; }

    /// <summary>Grid-view rows. Rebuilt when the column count changes. Slots are shared with <see cref="Pages"/>.</summary>
    public ObservableCollection<PageRowViewModel> Rows { get; }

    public ICollectionView PagesView { get; }

    public int PageCount => _pageSizes.Count;

    [ObservableProperty]
    private int _gridColumns = 1;

    /// <summary>Raised when the pane wants the view to bring a page into view (0-based).</summary>
    public event Action<int>? ScrollToPageRequested;

    [ObservableProperty]
    private ViewMode _mode = ViewMode.Continuous;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomPercentText))]
    private double _zoom;

    [ObservableProperty]
    private ZoomMode _zoomMode = ZoomMode.Custom;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    private int _currentPage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    [NotifyCanExecuteChangedFor(nameof(NextMatchCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousMatchCommand))]
    private int _totalMatches;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchSummary))]
    private int _currentMatchNumber;

    public string ZoomPercentText => $"{Math.Round(Zoom * 100)}%";

    public string PageCountText => $"/ {PageCount}";

    public string MatchSummary => TotalMatches > 0
        ? $"{CurrentMatchNumber} / {TotalMatches}"
        : string.IsNullOrWhiteSpace(SearchText) ? string.Empty : "No matches";

    // --- View → VM notifications -------------------------------------------------------------

    /// <summary>The view reports DPI so renders are produced at native device resolution.</summary>
    public void SetDeviceScale(double scale)
    {
        scale = scale <= 0 ? 1.0 : scale;
        if (Math.Abs(scale - _deviceScale) < 0.001)
        {
            return;
        }

        _deviceScale = scale;
        RerenderRealized(clearQueue: true);
    }

    /// <summary>The view reports its scroll viewport so Fit modes and the grid column count can be computed.</summary>
    public void SetViewport(double width, double height)
    {
        bool widthChanged = Math.Abs(width - _viewportWidth) > 0.5;
        _viewportWidth = width;
        _viewportHeight = height;

        if (ZoomMode != ZoomMode.Custom)
        {
            RecomputeFitZoom();
        }

        if (Mode == ViewMode.Grid && widthChanged)
        {
            RebuildGrid();
        }
    }

    /// <summary>Vertical gap (DIP) between stacked pages in continuous mode. The view must match this.</summary>
    public const double PageSpacing = 16.0;

    /// <summary>The view reports which page indices are on-screen (inclusive, 0-based).</summary>
    public void UpdateVisibleRange(int firstVisible, int lastVisible)
    {
        int first = Math.Max(0, firstVisible - GridColumns);
        int last = Math.Min(PageCount - 1, lastVisible + GridColumns);

        for (int i = 0; i < Pages.Count; i++)
        {
            bool visible = i >= first && i <= last;
            Pages[i].IsRealized = visible;
            if (visible)
            {
                RequestRender(Pages[i]);
            }
        }
    }

    /// <summary>Grid view: mark the pages of rows <paramref name="firstRow"/>..<paramref name="lastRow"/> visible.</summary>
    public void UpdateVisibleRows(int firstRow, int lastRow)
    {
        int columns = Math.Max(1, GridColumns);
        UpdateVisibleRange(firstRow * columns, ((lastRow + 1) * columns) - 1);
    }

    /// <summary>Distance (DIP) from the top of the stack to the top of <paramref name="pageIndex"/> (single column).</summary>
    public double GetPageOffset(int pageIndex)
    {
        double offset = 0;
        for (int i = 0; i < pageIndex && i < Pages.Count; i++)
        {
            offset += Pages[i].LayoutHeight + PageSpacing;
        }

        return offset;
    }

    /// <summary>Page index whose top is nearest at or above <paramref name="verticalOffset"/> (single column).</summary>
    public int GetPageAtOffset(double verticalOffset)
    {
        double running = 0;
        for (int i = 0; i < Pages.Count; i++)
        {
            running += Pages[i].LayoutHeight + PageSpacing;
            if (verticalOffset < running)
            {
                return i;
            }
        }

        return Math.Max(0, Pages.Count - 1);
    }

    /// <summary>Grid view: DIP offset to the top of <paramref name="rowIndex"/>.</summary>
    public double GetRowOffset(int rowIndex)
    {
        double offset = 0;
        for (int i = 0; i < rowIndex && i < Rows.Count; i++)
        {
            offset += Rows[i].RowHeight + PageSpacing;
        }

        return offset;
    }

    /// <summary>Grid view: row whose top is nearest at or above <paramref name="verticalOffset"/>.</summary>
    public int GetRowAtOffset(double verticalOffset)
    {
        double running = 0;
        for (int i = 0; i < Rows.Count; i++)
        {
            running += Rows[i].RowHeight + PageSpacing;
            if (verticalOffset < running)
            {
                return i;
            }
        }

        return Math.Max(0, Rows.Count - 1);
    }

    /// <summary>Grid view: DIP offset to the row that contains <paramref name="pageIndex"/>.</summary>
    public double GetRowOffsetForPage(int pageIndex) =>
        GetRowOffset(pageIndex / Math.Max(1, GridColumns));

    /// <summary>The view scrolled; <paramref name="topPageIndex"/> is the top-most visible page (0-based).</summary>
    public void ReportScrolledToPage(int topPageIndex)
    {
        if (_suppressScrollSync || Mode == ViewMode.SinglePage)
        {
            return;
        }

        int oneBased = topPageIndex + 1;
        if (oneBased >= 1 && oneBased <= PageCount && oneBased != CurrentPage)
        {
            _suppressScrollSync = true;
            CurrentPage = oneBased;
            _suppressScrollSync = false;
        }
    }

    // --- Navigation -------------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanGoNextPage))]
    private void NextPage() => GoToPage(CurrentPage + 1);

    private bool CanGoNextPage() => CurrentPage < PageCount;

    [RelayCommand(CanExecute = nameof(CanGoPreviousPage))]
    private void PreviousPage() => GoToPage(CurrentPage - 1);

    private bool CanGoPreviousPage() => CurrentPage > 1;

    public void GoToPage(int oneBasedPage)
    {
        int clamped = Math.Clamp(oneBasedPage, 1, Math.Max(1, PageCount));
        if (clamped == CurrentPage)
        {
            ScrollToPageRequested?.Invoke(clamped - 1);
            return;
        }

        CurrentPage = clamped;
    }

    partial void OnCurrentPageChanged(int value)
    {
        if (Mode == ViewMode.SinglePage)
        {
            PagesView.Refresh();
            RequestRenderForCurrentPage();
        }

        if (!_suppressScrollSync)
        {
            ScrollToPageRequested?.Invoke(value - 1);
        }
    }

    partial void OnModeChanged(ViewMode value)
    {
        if (value == ViewMode.Grid)
        {
            RebuildGrid();
        }
        else
        {
            ApplyLayout();
        }

        PagesView.Refresh();
        RerenderRealized(clearQueue: true);

        if (value == ViewMode.SinglePage)
        {
            RequestRenderForCurrentPage();
        }
        else
        {
            ScrollToPageRequested?.Invoke(CurrentPage - 1);
        }
    }

    // --- Zoom -----------------------------------------------------------------------------

    [RelayCommand]
    private void ZoomIn() => SetZoom(Zoom * ZoomStep, ZoomMode.Custom);

    [RelayCommand]
    private void ZoomOut() => SetZoom(Zoom / ZoomStep, ZoomMode.Custom);

    [RelayCommand]
    private void ActualSize() => SetZoom(1.0, ZoomMode.Custom);

    [RelayCommand]
    private void FitWidth()
    {
        ZoomMode = ZoomMode.FitWidth;
        RecomputeFitZoom();
    }

    [RelayCommand]
    private void FitPage()
    {
        ZoomMode = ZoomMode.FitPage;
        RecomputeFitZoom();
    }

    private void RecomputeFitZoom()
    {
        if (PageCount == 0 || _viewportWidth <= 0)
        {
            return;
        }

        PdfSize page = _pageSizes[Math.Clamp(CurrentPage - 1, 0, PageCount - 1)];
        double pageWidthDip = page.Width * PdfCoordinates.PointToDip;
        double pageHeightDip = page.Height * PdfCoordinates.PointToDip;

        double widthZoom = (_viewportWidth - FitMargin) / pageWidthDip;
        double zoom = widthZoom;

        if (ZoomMode == ZoomMode.FitPage && _viewportHeight > 0)
        {
            double heightZoom = (_viewportHeight - FitMargin) / pageHeightDip;
            zoom = Math.Min(widthZoom, heightZoom);
        }

        SetZoom(zoom, ZoomMode);
    }

    private void SetZoom(double value, ZoomMode mode)
    {
        double clamped = Math.Clamp(value, MinZoom, MaxZoom);
        ZoomMode = mode;

        if (Math.Abs(clamped - Zoom) < 0.0001)
        {
            return;
        }

        Zoom = clamped;
    }

    partial void OnZoomChanged(double value)
    {
        if (Mode == ViewMode.Grid)
        {
            RebuildGrid();
        }
        else
        {
            ApplyLayout();
        }

        RerenderRealized(clearQueue: true);
    }

    // --- Search ---------------------------------------------------------------------------

    [RelayCommand]
    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        ResetSearchState();

        string term = SearchText;
        if (string.IsNullOrWhiteSpace(term))
        {
            OnPropertyChanged(nameof(MatchSummary));
            return;
        }

        IsSearching = true;
        try
        {
            PdfSearchOptions options = MatchCase ? PdfSearchOptions.MatchCase : PdfSearchOptions.None;
            IReadOnlyList<SearchHit> hits =
                await Task.Run(() => _search.FindAll(term, options, cts.Token), cts.Token).ConfigureAwait(true);

            if (cts.Token.IsCancellationRequested)
            {
                return;
            }

            _hits = hits;
            TotalMatches = hits.Count;
            ApplyHighlights();
            if (hits.Count > 0)
            {
                MoveToHit(0);
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer search
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearching = false;
                _searchCts = null;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(HasMatches))]
    private void NextMatch()
    {
        if (_hits.Count > 0)
        {
            MoveToHit((_activeHit + 1) % _hits.Count);
        }
    }

    [RelayCommand(CanExecute = nameof(HasMatches))]
    private void PreviousMatch()
    {
        if (_hits.Count > 0)
        {
            MoveToHit((_activeHit - 1 + _hits.Count) % _hits.Count);
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        SearchText = string.Empty;
        ResetSearchState();
        OnPropertyChanged(nameof(MatchSummary));
    }

    private bool HasMatches() => _hits.Count > 0;

    private void ResetSearchState()
    {
        _hits = [];
        _activeHit = -1;
        TotalMatches = 0;
        CurrentMatchNumber = 0;
        foreach (PageSlotViewModel slot in Pages)
        {
            if (slot.Highlights.Count > 0)
            {
                slot.Highlights = [];
            }
        }
    }

    private void MoveToHit(int index)
    {
        _activeHit = index;
        CurrentMatchNumber = index + 1;
        SearchHit hit = _hits[index];
        ApplyHighlights();

        _suppressScrollSync = true;
        CurrentPage = hit.PageIndex + 1;
        _suppressScrollSync = false;

        if (Mode == ViewMode.SinglePage)
        {
            PagesView.Refresh();
            RequestRenderForCurrentPage();
        }

        ScrollToPageRequested?.Invoke(hit.PageIndex);
    }

    private void ApplyHighlights()
    {
        var byPage = _hits
            .Select((hit, index) => (hit, index))
            .GroupBy(x => x.hit.PageIndex)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (PageSlotViewModel slot in Pages)
        {
            if (!byPage.TryGetValue(slot.PageIndex, out var pageHits))
            {
                if (slot.Highlights.Count > 0)
                {
                    slot.Highlights = [];
                }

                continue;
            }

            double scale = slot.SizePoints.Width > 0
                ? slot.LayoutWidth / slot.SizePoints.Width
                : PdfCoordinates.PointToDip * Zoom;
            double pageHeightPoints = slot.SizePoints.Height;
            var rects = new List<HighlightRect>();

            foreach ((SearchHit hit, int hitIndex) in pageHits)
            {
                bool active = hitIndex == _activeHit;
                foreach (PdfRect rect in hit.Rects)
                {
                    DeviceRect d = PdfCoordinates.PageToDevice(rect, pageHeightPoints, scale);
                    rects.Add(new HighlightRect(new Rect(d.X, d.Y, d.Width, d.Height), active));
                }
            }

            slot.Highlights = rects;
        }
    }

    // --- Rendering ----------------------------------------------------------------------

    private void ApplyLayout()
    {
        if (Mode == ViewMode.Grid)
        {
            ApplyGridLayout();
        }
        else
        {
            double dipScale = PdfCoordinates.PointToDip * Zoom;
            foreach (PageSlotViewModel slot in Pages)
            {
                slot.LayoutWidth = slot.SizePoints.Width * dipScale;
                slot.LayoutHeight = slot.SizePoints.Height * dipScale;
            }
        }

        if (_hits.Count > 0)
        {
            ApplyHighlights();
        }
    }

    private void ApplyGridLayout()
    {
        int columns = Math.Max(1, GridColumns);
        double available = _viewportWidth > 0 ? _viewportWidth : columns * 220.0;
        double tile = ((available - FitMargin) - (PageSpacing * (columns - 1))) / columns;
        if (tile < 40)
        {
            tile = 40;
        }

        foreach (PageSlotViewModel slot in Pages)
        {
            double aspect = slot.SizePoints.Width > 0
                ? slot.SizePoints.Height / slot.SizePoints.Width
                : 1.294;
            slot.LayoutWidth = tile;
            slot.LayoutHeight = tile * aspect;
        }
    }

    /// <summary>Recompute the auto-fit column count, regroup the rows, and re-lay-out.</summary>
    private void RebuildGrid()
    {
        if (PageCount == 0)
        {
            return;
        }

        PdfSize reference = _pageSizes[Math.Clamp(CurrentPage - 1, 0, PageCount - 1)];
        double targetWidth = reference.Width * PdfCoordinates.PointToDip * Zoom;
        double available = _viewportWidth > 0 ? _viewportWidth - FitMargin : targetWidth;

        int columns = targetWidth > 0
            ? (int)Math.Round(available / (targetWidth + PageSpacing))
            : 1;
        columns = Math.Clamp(columns, 1, Math.Min(MaxGridColumns, Math.Max(1, PageCount)));

        if (columns != GridColumns)
        {
            GridColumns = columns;
        }

        ApplyGridLayout();
        RegroupRows(columns);

        if (_hits.Count > 0)
        {
            ApplyHighlights();
        }
    }

    private void RegroupRows(int columns)
    {
        Rows.Clear();
        for (int start = 0; start < Pages.Count; start += columns)
        {
            int count = Math.Min(columns, Pages.Count - start);
            var slots = new PageSlotViewModel[count];
            for (int i = 0; i < count; i++)
            {
                slots[i] = Pages[start + i];
            }

            Rows.Add(new PageRowViewModel(start / columns, slots));
        }
    }

    private void RerenderRealized(bool clearQueue)
    {
        if (clearQueue)
        {
            _queue.Clear();
        }

        foreach (PageSlotViewModel slot in Pages)
        {
            if (slot.IsRealized)
            {
                RequestRender(slot);
            }
        }
    }

    private void RequestRenderForCurrentPage()
    {
        if (CurrentPage >= 1 && CurrentPage <= Pages.Count)
        {
            RequestRender(Pages[CurrentPage - 1]);
        }
    }

    private void RequestRender(PageSlotViewModel slot)
    {
        int pixelWidth = ExpectedPixelWidth(slot);
        int pixelHeight = ExpectedPixelHeight(slot);
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return;
        }

        if (slot.Image is not null && slot.RenderedPixelWidth == pixelWidth)
        {
            return;
        }

        _queue.Enqueue(new RenderRequest
        {
            Owner = this,
            Document = _document,
            PageIndex = slot.PageIndex,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            OnRendered = OnPageRendered,
        });
    }

    private void OnPageRendered(int pageIndex, int pixelWidth, ImageSource image)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
        {
            return;
        }

        PageSlotViewModel slot = Pages[pageIndex];
        if (pixelWidth != ExpectedPixelWidth(slot))
        {
            return; // stale (zoom/DPI changed since this was queued)
        }

        slot.RenderedPixelWidth = pixelWidth;
        slot.Image = image;
    }

    // Renders are produced at the slot's exact on-screen size (which already folds in zoom,
    // grid tiling and DPI), so a stale render from a previous layout is easy to detect.
    private int ExpectedPixelWidth(PageSlotViewModel slot) =>
        (int)Math.Round(slot.LayoutWidth * _deviceScale);

    private int ExpectedPixelHeight(PageSlotViewModel slot) =>
        (int)Math.Round(slot.LayoutHeight * _deviceScale);

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}
