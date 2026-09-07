using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.App.Views;
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
    private IReadOnlyList<PdfSize> _pageSizes;

    private CancellationTokenSource? _searchCts;
    private IReadOnlyList<SearchHit> _hits = [];
    private int _activeHit = -1;
    private double _deviceScale = 1.0;
    private double _viewportWidth;
    private double _viewportHeight;
    private bool _suppressScrollSync;
    private bool _deferRerender;
    private DispatcherTimer? _rerenderTimer;

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
        PagesView.Filter = o =>
        {
            if (o is not PageSlotViewModel slot)
            {
                return true;
            }

            return Mode switch
            {
                ViewMode.SinglePage => slot.PageIndex == CurrentPage - 1,
                ViewMode.TwoPage => slot.PageIndex == SpreadLeftIndex || slot.PageIndex == SpreadLeftIndex + 1,
                _ => true,
            };
        };

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

    /// <summary>Raised after <see cref="ReloadPages"/> rebuilds the page list (the view re-initialises).</summary>
    public event Action? PagesReloaded;

    /// <summary>
    /// Rebuild the page list after the document's pages changed (rotate / delete / insert / undo).
    /// Search state is dropped because hit coordinates and page indices are no longer valid.
    /// </summary>
    public void ReloadPages(IReadOnlyList<PdfSize> pageSizes)
    {
        _pageSizes = pageSizes;

        ResetSearchState();
        SearchText = string.Empty;
        _searchCts?.Cancel();
        _searchCts = null;
        _charBoxCache.Clear();
        _linkCache.Clear();
        ClearTextSelection();

        Pages.Clear();
        for (int i = 0; i < pageSizes.Count; i++)
        {
            Pages.Add(new PageSlotViewModel(i, pageSizes[i]));
        }

        int clamped = PageCount == 0 ? 0 : Math.Clamp(CurrentPage, 1, PageCount);
        _suppressScrollSync = true;
        CurrentPage = clamped;
        _suppressScrollSync = false;

        if (Mode == ViewMode.Grid)
        {
            RebuildGrid();
        }
        else
        {
            ApplyLayout();
        }

        PagesView.Refresh();
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageCountText));
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        PagesReloaded?.Invoke();
    }

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
        else if (Mode == ViewMode.TwoPage)
        {
            ApplyTwoPageLayout();
            RerenderRealized(clearQueue: false);
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

        BuildLinkOverlays();
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
        if (_suppressScrollSync || IsPaged)
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

    /// <summary>One screenful at a time (no vertical scroll to sync): single page, or a spread.</summary>
    public bool IsPaged => Mode is ViewMode.SinglePage or ViewMode.TwoPage;

    /// <summary>0-based index of the left page of the spread that holds <see cref="CurrentPage"/>.</summary>
    public int SpreadLeftIndex => ((CurrentPage - 1) / 2) * 2;

    [RelayCommand(CanExecute = nameof(CanGoNextPage))]
    private void NextPage() => GoToPage(CurrentPage + (Mode == ViewMode.TwoPage ? 2 : 1));

    private bool CanGoNextPage() =>
        Mode == ViewMode.TwoPage ? SpreadLeftIndex + 2 < PageCount : CurrentPage < PageCount;

    [RelayCommand(CanExecute = nameof(CanGoPreviousPage))]
    private void PreviousPage() => GoToPage(CurrentPage - (Mode == ViewMode.TwoPage ? 2 : 1));

    private bool CanGoPreviousPage() =>
        Mode == ViewMode.TwoPage ? SpreadLeftIndex > 0 : CurrentPage > 1;

    /// <summary>Turn one spread (or page) — <paramref name="direction"/> is +1 forward, -1 back.</summary>
    public void Advance(int direction)
    {
        if (direction > 0 && NextPageCommand.CanExecute(null))
        {
            NextPage();
        }
        else if (direction < 0 && PreviousPageCommand.CanExecute(null))
        {
            PreviousPage();
        }
    }

    public void GoToPage(int oneBasedPage)
    {
        int clamped = Math.Clamp(oneBasedPage, 1, Math.Max(1, PageCount));
        if (Mode == ViewMode.TwoPage)
        {
            clamped = Math.Min(PageCount, (((clamped - 1) / 2) * 2) + 1); // snap to the spread's left page
        }

        if (clamped == CurrentPage)
        {
            ScrollToPageRequested?.Invoke(clamped - 1);
            return;
        }

        CurrentPage = clamped;
    }

    partial void OnCurrentPageChanged(int value)
    {
        if (IsPaged)
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
            if (value == ViewMode.TwoPage)
            {
                _suppressScrollSync = true;
                CurrentPage = Math.Min(PageCount, SpreadLeftIndex + 1);
                _suppressScrollSync = false;
            }

            ApplyLayout();
        }

        PagesView.Refresh();
        RerenderRealized(clearQueue: true);
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();

        if (IsPaged)
        {
            RequestRenderForCurrentPage();
        }
        else
        {
            ScrollToPageRequested?.Invoke(CurrentPage - 1);
        }
    }

    // --- Zoom -----------------------------------------------------------------------------

    /// <summary>
    /// Ctrl+wheel / trackpad pinch zoom. <paramref name="wheelDelta"/> is the raw delta
    /// (±120 per mouse notch, small values from a precision trackpad); the factor is smooth so a
    /// single notch ≈ one <see cref="ZoomIn"/> step.
    /// </summary>
    public void ZoomByWheel(int wheelDelta)
    {
        if (wheelDelta == 0)
        {
            return;
        }

        _deferRerender = true;
        try
        {
            SetZoom(Zoom * Math.Pow(1.0015, wheelDelta), ZoomMode.Custom);
        }
        finally
        {
            _deferRerender = false;
        }
    }

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

        if (_deferRerender)
        {
            // Wheel / pinch zoom: let the old bitmaps stretch during the gesture, re-render once
            // it settles.
            _rerenderTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _rerenderTimer.Tick -= OnRerenderTimerTick;
            _rerenderTimer.Tick += OnRerenderTimerTick;
            _rerenderTimer.Stop();
            _rerenderTimer.Start();
        }
        else
        {
            _rerenderTimer?.Stop();
            RerenderRealized(clearQueue: true);
        }
    }

    private void OnRerenderTimerTick(object? sender, EventArgs e)
    {
        _rerenderTimer!.Stop();
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
        CurrentPage = Mode == ViewMode.TwoPage
            ? Math.Min(PageCount, ((hit.PageIndex / 2) * 2) + 1)
            : hit.PageIndex + 1;
        _suppressScrollSync = false;

        if (IsPaged)
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

            double scale = SlotScale(slot);
            PdfSize unrotated = _document.GetUnrotatedPageSize(slot.PageIndex);
            PdfRotation rotation = _document.GetPageRotation(slot.PageIndex);
            PdfPoint crop = _document.GetCropOrigin(slot.PageIndex);
            var rects = new List<HighlightRect>();

            foreach ((SearchHit hit, int hitIndex) in pageHits)
            {
                bool active = hitIndex == _activeHit;
                foreach (PdfRect rect in hit.Rects)
                {
                    DeviceRect d = PdfCoordinates.PageToDevice(rect, unrotated, rotation, scale, crop);
                    rects.Add(new HighlightRect(new Rect(d.X, d.Y, d.Width, d.Height), active));
                }
            }

            slot.Highlights = rects;
        }
    }

    // --- Annotations ------------------------------------------------------------------------

    private readonly Dictionary<int, IReadOnlyList<PdfCharBox>> _charBoxCache = [];

    private int _selectionPage = -1;
    private int _selectionAnchor = -1;
    private int _selectionHead = -1;
    private IReadOnlyList<PdfRect> _pendingQuads = [];

    [ObservableProperty]
    private bool _commentToolActive;

    /// <summary>When on, dragging on a page draws a freehand highlighter stroke. Mirrored to both panes.</summary>
    [ObservableProperty]
    private bool _highlighterToolActive;

    /// <summary>Palette key the highlighter pen uses.</summary>
    [ObservableProperty]
    private string _inkColorKey = AnnotationColors.Default;

    /// <summary>Highlighter stroke width, in PDF points.</summary>
    private const double InkStrokeWidthPoints = 12.0;

    private int _inkPage = -1;
    private List<PdfPoint>? _inkStroke;

    /// <summary>Id of the annotation to draw as selected (e.g. picked from the annotations list).</summary>
    [ObservableProperty]
    private System.Guid? _selectedAnnotationId;

    /// <summary>Page a finished text selection is on, or -1. The view watches this to raise the popup.</summary>
    public int PendingSelectionPage { get; private set; } = -1;

    /// <summary>Bounding box of a finished text selection, in that page slot's DIP space.</summary>
    public Rect PendingSelectionBounds { get; private set; }

    public bool HasPendingSelection => PendingSelectionPage >= 0 && _pendingQuads.Count > 0;

    /// <summary>Author stamped on annotations this session.</summary>
    private static string Author => System.Environment.UserName;

    /// <summary>The fixed highlight palette, for the selection popup.</summary>
    public IReadOnlyList<string> HighlightColorKeys => AnnotationColors.Keys;

    /// <summary>Map a point within a page slot (DIP, top-left origin) to unrotated page space
    /// (MediaBox origin — the space char boxes, quads and ink points live in).</summary>
    public PdfPoint DevicePointToPage(int pageIndex, double deviceX, double deviceY)
    {
        PageSlotViewModel slot = Pages[pageIndex];
        return PdfCoordinates.DeviceToPage(deviceX, deviceY,
            _document.GetUnrotatedPageSize(pageIndex), _document.GetPageRotation(pageIndex),
            SlotScale(slot), _document.GetCropOrigin(pageIndex));
    }

    partial void OnSelectedAnnotationIdChanged(System.Guid? value) => BuildAnnotationOverlays();

    /// <summary>
    /// Select the topmost highlight band or ink stroke under <paramref name="pt"/> (unrotated
    /// page space). Returns true when one was hit; clears the selection and returns false otherwise.
    /// </summary>
    public bool TrySelectAnnotationAt(int pageIndex, PdfPoint pt)
    {
        if (!AnnotationsVisible)
        {
            return false;
        }

        IReadOnlyList<PdfAnnotation> annotations = _document.GetAnnotations(pageIndex);
        for (int i = annotations.Count - 1; i >= 0; i--)
        {
            PdfAnnotation a = annotations[i];

            if (a.Kind == PdfAnnotationKind.Highlight)
            {
                foreach (PdfRect q in a.Quads)
                {
                    if (pt.X >= q.Left && pt.X <= q.Right && pt.Y >= q.Bottom && pt.Y <= q.Top)
                    {
                        SelectedAnnotationId = a.Id;
                        return true;
                    }
                }
            }
            else if (a.Kind == PdfAnnotationKind.Ink && HitsInk(a, pt))
            {
                SelectedAnnotationId = a.Id;
                return true;
            }
            else if (a.Kind is PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout or PdfAnnotationKind.Cloud)
            {
                PdfRect b = a.Box;
                double left = Math.Min(b.Left, b.Right), right = Math.Max(b.Left, b.Right);
                double bottom = Math.Min(b.Top, b.Bottom), top = Math.Max(b.Top, b.Bottom);
                if (pt.X >= left && pt.X <= right && pt.Y >= bottom && pt.Y <= top)
                {
                    SelectedAnnotationId = a.Id;
                    return true;
                }
            }
        }

        SelectedAnnotationId = null;
        return false;
    }

    private static bool HitsInk(PdfAnnotation ink, PdfPoint pt)
    {
        double tolerance = (Math.Max(1, ink.StrokeWidth) / 2) + 3;
        double toleranceSq = tolerance * tolerance;

        foreach (IReadOnlyList<PdfPoint> stroke in ink.Strokes)
        {
            for (int i = 1; i < stroke.Count; i++)
            {
                if (DistanceSqToSegment(pt, stroke[i - 1], stroke[i]) <= toleranceSq)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static double DistanceSqToSegment(PdfPoint p, PdfPoint a, PdfPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = (dx * dx) + (dy * dy);
        double t = lenSq <= 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lenSq, 0, 1);
        double cx = a.X + (t * dx), cy = a.Y + (t * dy);
        return ((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy));
    }

    /// <summary>When false, the annotation overlay is hidden on every page (a view-only toggle;
    /// the annotations are untouched and still saved).</summary>
    [ObservableProperty]
    private bool _annotationsVisible = true;

    partial void OnAnnotationsVisibleChanged(bool value) => BuildAnnotationOverlays();

    /// <summary>Project every page's annotations into its slot's DIP space for the overlay.</summary>
    public void BuildAnnotationOverlays()
    {
        foreach (PageSlotViewModel slot in Pages)
        {
            IReadOnlyList<PdfAnnotation> annotations = AnnotationsVisible
                ? _document.GetAnnotations(slot.PageIndex)
                : [];
            if (annotations.Count == 0)
            {
                if (slot.Annotations.Count > 0)
                {
                    slot.Annotations = [];
                }

                continue;
            }

            double scale = SlotScale(slot);
            PdfSize unrotated = _document.GetUnrotatedPageSize(slot.PageIndex);
            PdfRotation rotation = _document.GetPageRotation(slot.PageIndex);
            PdfPoint crop = _document.GetCropOrigin(slot.PageIndex);

            var visuals = new List<AnnotationVisual>(annotations.Count);
            foreach (PdfAnnotation stored in annotations)
            {
                // Show a shape being dragged / resized / re-pointed at its live offset without committing it.
                PdfAnnotation annotation =
                    stored.Id == _movingShapeId ? MoveShape(stored, _moveOffsetPage.Dx, _moveOffsetPage.Dy)
                    : stored.Id == _resizingShapeId ? ResizeShape(stored, _resizeHandle, _resizeDeltaPage.Dx, _resizeDeltaPage.Dy)
                    : stored.Id == _leaderTipId ? MoveLeaderTip(stored, _leaderTipDeltaPage.Dx, _leaderTipDeltaPage.Dy)
                    : stored;

                var rects = new List<Rect>();
                if (annotation.Kind == PdfAnnotationKind.Highlight)
                {
                    foreach (PdfRect quad in annotation.Quads)
                    {
                        DeviceRect d = PdfCoordinates.PageToDevice(quad, unrotated, rotation, scale, crop);
                        rects.Add(new Rect(d.X, d.Y, d.Width, d.Height));
                    }
                }

                var strokes = new List<PointCollection>();
                if (annotation.Kind == PdfAnnotationKind.Ink)
                {
                    foreach (IReadOnlyList<PdfPoint> stroke in annotation.Strokes)
                    {
                        var pts = new PointCollection(stroke.Count);
                        foreach (PdfPoint p in stroke)
                        {
                            (double x, double y) = PdfCoordinates.PageToDevicePoint(p, unrotated, rotation, scale, crop);
                            pts.Add(new Point(x, y));
                        }

                        strokes.Add(pts);
                    }
                }

                DeviceRect bounds = PdfCoordinates.PageToDevice(annotation.Bounds, unrotated, rotation, scale, crop);
                var marker = new Rect(Math.Max(0, bounds.X + bounds.Width - 8), Math.Max(0, bounds.Y - 6), 17, 17);

                Rect box = default;
                var leader = new PointCollection();
                var leaderArrow = new PointCollection();
                var handles = new List<Rect>();
                Rect leaderTipHandle = Rect.Empty;
                Geometry? cloud = null;
                if (annotation.Kind is PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout or PdfAnnotationKind.Cloud)
                {
                    DeviceRect d = PdfCoordinates.PageToDevice(annotation.Box, unrotated, rotation, scale, crop);
                    box = new Rect(d.X, d.Y, d.Width, d.Height);

                    foreach (PdfPoint p in annotation.Leader)
                    {
                        (double x, double y) = PdfCoordinates.PageToDevicePoint(p, unrotated, rotation, scale, crop);
                        leader.Add(new Point(x, y));
                    }

                    if (leader.Count >= 2)
                    {
                        leaderArrow = LeaderArrowHead(leader[0], leader[1]);
                    }

                    if (annotation.Kind == PdfAnnotationKind.Cloud)
                    {
                        cloud = ShapeGeometry.Cloud(box, 8 * scale);
                    }

                    if (annotation.Id == SelectedAnnotationId)
                    {
                        const double hs = 9;
                        foreach (Point c in new[]
                        {
                            box.TopLeft, box.TopRight, box.BottomLeft, box.BottomRight,
                        })
                        {
                            handles.Add(new Rect(c.X - (hs / 2), c.Y - (hs / 2), hs, hs));
                        }

                        if (annotation.Kind == PdfAnnotationKind.Callout && leader.Count >= 1)
                        {
                            const double ts = 11;
                            leaderTipHandle = new Rect(leader[0].X - (ts / 2), leader[0].Y - (ts / 2), ts, ts);
                        }
                    }
                }

                visuals.Add(new AnnotationVisual(annotation.Id, annotation.Kind, rects, marker,
                    AnnotationColors.ToColor(annotation.ColorArgb), annotation.HasNote,
                    annotation.Id == SelectedAnnotationId)
                {
                    Strokes = strokes,
                    StrokeThickness = Math.Max(1, annotation.StrokeWidth * scale),
                    Box = box,
                    Leader = leader,
                    LeaderArrow = leaderArrow,
                    ResizeHandles = handles,
                    LeaderTipHandle = leaderTipHandle,
                    CloudGeometry = cloud,
                    BoxText = annotation.Contents ?? string.Empty,
                    BoxFontSize = Math.Max(4, (annotation.FontSize > 0 ? annotation.FontSize : PdfAnnotation.DefaultFontSize) * scale),
                });
            }

            slot.Annotations = visuals;
        }
    }

    private double SlotScale(PageSlotViewModel slot) =>
        slot.SizePoints.Width > 0 ? slot.LayoutWidth / slot.SizePoints.Width : PdfCoordinates.PointToDip * Zoom;

    // --- In-document links ----------------------------------------------------------------

    /// <summary>Raw link regions per page (from PDFium), read once and kept until the pages reload.</summary>
    private readonly Dictionary<int, IReadOnlyList<PdfLink>> _linkCache = [];

    /// <summary>
    /// Project each realised page's <c>/Link</c> regions into its slot's DIP space. Reads are
    /// lazy and cached, so this is cheap to call on every scroll / zoom / layout change.
    /// </summary>
    public void BuildLinkOverlays()
    {
        foreach (PageSlotViewModel slot in Pages)
        {
            if (!slot.IsRealized)
            {
                if (slot.Links.Count > 0)
                {
                    slot.Links = [];
                }

                continue;
            }

            if (!_linkCache.TryGetValue(slot.PageIndex, out IReadOnlyList<PdfLink>? links))
            {
                links = PdfLinks.Read(_document, slot.PageIndex);
                _linkCache[slot.PageIndex] = links;
            }

            if (links.Count == 0)
            {
                if (slot.Links.Count > 0)
                {
                    slot.Links = [];
                }

                continue;
            }

            double scale = SlotScale(slot);
            PdfSize unrotated = _document.GetUnrotatedPageSize(slot.PageIndex);
            PdfRotation rotation = _document.GetPageRotation(slot.PageIndex);
            PdfPoint crop = _document.GetCropOrigin(slot.PageIndex);

            var visuals = new List<LinkVisual>(links.Count);
            foreach (PdfLink link in links)
            {
                DeviceRect d = PdfCoordinates.PageToDevice(link.Rect, unrotated, rotation, scale, crop);
                visuals.Add(new LinkVisual(new Rect(d.X, d.Y, d.Width, d.Height), link.TargetPageIndex, link.Uri));
            }

            slot.Links = visuals;
        }
    }

    /// <summary>Follow a link the user clicked: jump to its page, or open its URL.</summary>
    [RelayCommand]
    private void FollowLink(LinkVisual? link)
    {
        if (link is null)
        {
            return;
        }

        if (link.TargetPageIndex is int page)
        {
            GoToPage(page + 1);
            return;
        }

        if (link.Uri is { Length: > 0 } uri
            && Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
            && parsed.Scheme is "http" or "https" or "mailto")
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parsed.AbsoluteUri)
                {
                    UseShellExecute = true,
                });
            }
            catch (System.Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Nothing to do — the OS declined to open the link.
            }
        }
    }

    // --- Text selection (driven by PdfPaneView mouse handlers) --------------------------

    public void BeginTextSelection(int pageIndex, PdfPoint pagePoint)
    {
        ClearTextSelection();
        _selectionPage = pageIndex;
        _selectionAnchor = _selectionHead = NearestCharIndex(pageIndex, pagePoint);
        UpdateSelectionRects();
    }

    public void ExtendTextSelection(PdfPoint pagePoint)
    {
        if (_selectionPage < 0)
        {
            return;
        }

        _selectionHead = NearestCharIndex(_selectionPage, pagePoint);
        UpdateSelectionRects();
    }

    /// <summary>Finish a drag. Returns true when a non-empty run of text was selected.</summary>
    public bool EndTextSelection()
    {
        if (_selectionPage < 0 || _selectionAnchor < 0 || _selectionHead < 0)
        {
            ClearTextSelection();
            return false;
        }

        int lo = Math.Min(_selectionAnchor, _selectionHead);
        int hi = Math.Max(_selectionAnchor, _selectionHead);
        IReadOnlyList<PdfRect> quads = BuildLineQuads(_selectionPage, lo, hi);
        if (quads.Count == 0)
        {
            ClearTextSelection();
            return false;
        }

        _pendingQuads = quads;
        PendingSelectionPage = _selectionPage;

        double scale = SlotScale(Pages[_selectionPage]);
        PdfSize unrotated = _document.GetUnrotatedPageSize(_selectionPage);
        PdfRotation rotation = _document.GetPageRotation(_selectionPage);
        PdfPoint crop = _document.GetCropOrigin(_selectionPage);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (PdfRect quad in quads)
        {
            DeviceRect d = PdfCoordinates.PageToDevice(quad, unrotated, rotation, scale, crop);
            minX = Math.Min(minX, d.X);
            minY = Math.Min(minY, d.Y);
            maxX = Math.Max(maxX, d.X + d.Width);
            maxY = Math.Max(maxY, d.Y + d.Height);
        }

        PendingSelectionBounds = new Rect(minX, minY, maxX - minX, maxY - minY);
        OnPropertyChanged(nameof(HasPendingSelection));
        return true;
    }

    public void ClearTextSelection()
    {
        _selectionPage = _selectionAnchor = _selectionHead = -1;
        _pendingQuads = [];
        PendingSelectionPage = -1;
        foreach (PageSlotViewModel slot in Pages)
        {
            if (slot.SelectionRects.Count > 0)
            {
                slot.SelectionRects = [];
            }
        }

        OnPropertyChanged(nameof(HasPendingSelection));
    }

    // --- Freehand highlighter (driven by PdfPaneView mouse handlers) --------------------

    public void BeginInk(int pageIndex, PdfPoint pagePoint)
    {
        _inkPage = pageIndex;
        _inkStroke = [pagePoint];
        UpdateInkPreview();
    }

    public void ExtendInk(PdfPoint pagePoint)
    {
        if (_inkStroke is null)
        {
            return;
        }

        PdfPoint last = _inkStroke[^1];
        double dx = pagePoint.X - last.X, dy = pagePoint.Y - last.Y;
        if ((dx * dx) + (dy * dy) < 1.5) // ~1.2pt: keep the polyline light
        {
            return;
        }

        _inkStroke.Add(pagePoint);
        UpdateInkPreview();
    }

    /// <summary>Finish a highlighter stroke; commits it as an ink annotation when it has length.</summary>
    public void EndInk()
    {
        List<PdfPoint>? stroke = _inkStroke;
        int page = _inkPage;
        _inkStroke = null;
        _inkPage = -1;
        ClearInkPreview();

        if (stroke is null || page < 0 || stroke.Count < 2)
        {
            return; // a stray click with no drag draws nothing
        }

        _document.AddAnnotation(page, PdfAnnotation.NewInk(
            [stroke], AnnotationColors.ToArgb(InkColorKey), InkStrokeWidthPoints, Author));
    }

    private void UpdateInkPreview()
    {
        if (_inkStroke is null || _inkPage < 0)
        {
            return;
        }

        PageSlotViewModel slot = Pages[_inkPage];
        double scale = SlotScale(slot);
        PdfSize unrotated = _document.GetUnrotatedPageSize(_inkPage);
        PdfRotation rotation = _document.GetPageRotation(_inkPage);
        PdfPoint crop = _document.GetCropOrigin(_inkPage);

        var pts = new System.Windows.Media.PointCollection(_inkStroke.Count);
        foreach (PdfPoint p in _inkStroke)
        {
            (double x, double y) = PdfCoordinates.PageToDevicePoint(p, unrotated, rotation, scale, crop);
            pts.Add(new Point(x, y));
        }

        slot.InkPreview = pts;
        slot.InkPreviewBrush = AnnotationColors.ToColor(InkColorKey);
        slot.InkPreviewThickness = Math.Max(1, InkStrokeWidthPoints * scale);
    }

    private void ClearInkPreview()
    {
        foreach (PageSlotViewModel slot in Pages)
        {
            if (slot.InkPreview is not null)
            {
                slot.InkPreview = null;
            }
        }
    }

    // --- Shape tools: drag a rectangle to drop a text box / cloud ------------------------

    /// <summary>The armed shape tool (mirrored from the tab). Cleared once a shape is placed.</summary>
    [ObservableProperty]
    private ShapeTool _shapeTool;

    private int _shapePage = -1;
    private PdfPoint _shapeAnchor;
    private PdfPoint _shapeHead;

    public void BeginShape(int pageIndex, PdfPoint pagePoint)
    {
        _shapePage = pageIndex;
        _shapeAnchor = _shapeHead = pagePoint;
        UpdateShapePreview();
    }

    public void ExtendShape(PdfPoint pagePoint)
    {
        if (_shapePage < 0)
        {
            return;
        }

        _shapeHead = pagePoint;
        UpdateShapePreview();
    }

    /// <summary>Finish the drag; drops the shape and (for text kinds) opens the editor for its text.</summary>
    public void EndShape()
    {
        int page = _shapePage;
        PdfPoint a = _shapeAnchor, b = _shapeHead;
        ShapeTool tool = ShapeTool;
        _shapePage = -1;
        ClearShapePreview();

        if (page < 0)
        {
            return;
        }

        ShapeTool = ShapeTool.None; // one-shot

        if (tool == ShapeTool.Callout)
        {
            // Drag from the thing you're pointing at (a = tip) to where the note goes (b).
            PdfPoint tip = a, boxCorner = b;
            uint calloutColor = AnnotationColors.ToArgb(InkColorKey);
            OpenEditor(PdfAnnotationKind.Callout, string.Empty, AnnotationColors.FromArgb(calloutColor),
                canDelete: false, Author, System.DateTimeOffset.Now, null, result =>
                {
                    if (result.Outcome == AnnotationEditorOutcome.Save)
                    {
                        string? text = string.IsNullOrWhiteSpace(result.Contents) ? null : result.Contents;
                        PdfRect cBox = FitTextBox(DefaultBoxAt(boxCorner, 190, 60), text, result.FontSize);
                        _document.AddAnnotation(page, PdfAnnotation.NewCallout(
                            cBox, [tip, BoxAttachPoint(cBox, tip)], text,
                            AnnotationColors.ToArgb(result.ColorKey), result.FontSize, Author)
                            with { Replies = result.Replies });
                    }
                });
            return;
        }

        var box = new PdfRect(
            Math.Min(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.X, b.X), Math.Min(a.Y, b.Y));

        // A stray click with no real drag: give a sensible default box so the tool still works.
        if (box.Width < 8 || box.Height < 8)
        {
            box = tool == ShapeTool.Cloud ? DefaultBoxAt(a, 160, 90) : DefaultBoxAt(a, 180, 60);
        }

        if (tool == ShapeTool.Cloud)
        {
            _document.AddAnnotation(page, PdfAnnotation.NewCloud(box, AnnotationColors.ToArgb(InkColorKey), Author));
            return;
        }

        // Text box: place it, then open the editor for the text/colour.
        PdfRect draggedBox = box;
        uint color = AnnotationColors.ToArgb(InkColorKey);
        OpenEditor(PdfAnnotationKind.TextBox, string.Empty, AnnotationColors.FromArgb(color), canDelete: false,
            Author, System.DateTimeOffset.Now, null, result =>
            {
                if (result.Outcome == AnnotationEditorOutcome.Save)
                {
                    string? text = string.IsNullOrWhiteSpace(result.Contents) ? null : result.Contents;
                    _document.AddAnnotation(page, PdfAnnotation.NewTextBox(
                        FitTextBox(draggedBox, text, result.FontSize), text,
                        AnnotationColors.ToArgb(result.ColorKey), result.FontSize, Author)
                        with { Replies = result.Replies });
                }
            });
    }

    /// <summary>Shrink-wrap <paramref name="box"/> to just fit <paramref name="text"/> at
    /// <paramref name="fontSize"/> — never wider than the box, keeping its top-left anchored.</summary>
    private static PdfRect FitTextBox(PdfRect box, string? text, double fontSize)
    {
        if (fontSize <= 0)
        {
            fontSize = PdfAnnotation.DefaultFontSize;
        }

        double maxW = box.Width >= 40 ? box.Width : 220;
        (double w, double h) = PdfTextWrap.FittedSize(text, maxW, fontSize);
        double width = Math.Max(40, w);
        double height = Math.Max(fontSize * 1.7, h);
        return new PdfRect(box.Left, box.Top, box.Left + width, box.Top - height);
    }

    /// <summary>Keep a user-sized box's width but grow its height if the text no longer fits (never shrink).</summary>
    private static PdfRect GrowToFitText(PdfRect box, string? text, double fontSize)
    {
        if (fontSize <= 0)
        {
            fontSize = PdfAnnotation.DefaultFontSize;
        }

        double top = Math.Max(box.Top, box.Bottom), bottom = Math.Min(box.Top, box.Bottom);
        double left = Math.Min(box.Left, box.Right), right = Math.Max(box.Left, box.Right);
        (_, double needed) = PdfTextWrap.FittedSize(text, right - left, fontSize);
        double height = Math.Max(top - bottom, needed);
        return new PdfRect(left, top, right, top - height);
    }

    /// <summary>A box of the given point size with its top-left at <paramref name="topLeft"/>.</summary>
    private static PdfRect DefaultBoxAt(PdfPoint topLeft, double width, double height) =>
        new(topLeft.X, topLeft.Y, topLeft.X + width, topLeft.Y - height);

    /// <summary>The point on the edge of <paramref name="box"/> that a leader from <paramref name="target"/>
    /// should attach to (nearest edge midpoint).</summary>
    private static PdfPoint BoxAttachPoint(PdfRect box, PdfPoint target)
    {
        double l = Math.Min(box.Left, box.Right), r = Math.Max(box.Left, box.Right);
        double bt = Math.Min(box.Top, box.Bottom), tp = Math.Max(box.Top, box.Bottom);
        double cx = (l + r) / 2, cy = (bt + tp) / 2;

        if (target.X < l) return new PdfPoint(l, cy);
        if (target.X > r) return new PdfPoint(r, cy);
        return target.Y > cy ? new PdfPoint(cx, tp) : new PdfPoint(cx, bt);
    }

    // --- Moving a placed shape box -------------------------------------------------------

    private System.Guid? _movingShapeId;
    private (double Dx, double Dy) _moveOffsetPage;

    /// <summary>The topmost text box / callout / cloud whose box contains <paramref name="pt"/>, or null.</summary>
    public System.Guid? TryHitShapeBox(int pageIndex, PdfPoint pt)
    {
        if (!AnnotationsVisible)
        {
            return null;
        }

        IReadOnlyList<PdfAnnotation> annotations = _document.GetAnnotations(pageIndex);
        for (int i = annotations.Count - 1; i >= 0; i--)
        {
            PdfAnnotation a = annotations[i];
            if (a.Kind is not (PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout or PdfAnnotationKind.Cloud))
            {
                continue;
            }

            PdfRect b = a.Box;
            double l = Math.Min(b.Left, b.Right), r = Math.Max(b.Left, b.Right);
            double bt = Math.Min(b.Top, b.Bottom), tp = Math.Max(b.Top, b.Bottom);
            if (pt.X >= l && pt.X <= r && pt.Y >= bt && pt.Y <= tp)
            {
                return a.Id;
            }
        }

        return null;
    }

    public void BeginShapeMove(System.Guid id)
    {
        _movingShapeId = id;
        _moveOffsetPage = (0, 0);
        SelectedAnnotationId = id;
    }

    public void PreviewShapeMove(double dxPage, double dyPage)
    {
        if (_movingShapeId is null)
        {
            return;
        }

        _moveOffsetPage = (dxPage, dyPage);
        BuildAnnotationOverlays();
    }

    /// <summary>Commit a move; a delta under ~1pt is treated as a click (no edit).</summary>
    public void EndShapeMove(double dxPage, double dyPage)
    {
        System.Guid? id = _movingShapeId;
        _movingShapeId = null;
        _moveOffsetPage = (0, 0);
        if (id is null)
        {
            return;
        }

        (int page, PdfAnnotation? found) = FindAnnotation(id.Value);
        if (found is not { } a || (Math.Abs(dxPage) < 1 && Math.Abs(dyPage) < 1))
        {
            BuildAnnotationOverlays();
            return;
        }

        _document.UpdateAnnotation(page, MoveShape(a, dxPage, dyPage) with { Modified = System.DateTimeOffset.Now });
    }

    public void CancelShapeMove()
    {
        _movingShapeId = null;
        _moveOffsetPage = (0, 0);
        BuildAnnotationOverlays();
    }

    /// <summary>Offset a shape's box by (dx, dy) page points; a callout keeps its tip and re-attaches the leader.</summary>
    private static PdfAnnotation MoveShape(PdfAnnotation a, double dx, double dy)
    {
        var box = new PdfRect(a.Box.Left + dx, a.Box.Top + dy, a.Box.Right + dx, a.Box.Bottom + dy);
        if (a.Kind == PdfAnnotationKind.Callout && a.Leader.Count >= 1)
        {
            PdfPoint tip = a.Leader[0];
            return a with { Quads = [box], Strokes = [new[] { tip, BoxAttachPoint(box, tip) }] };
        }

        return a with { Quads = [box] };
    }

    // --- Resizing a placed shape box ----------------------------------------------------

    private const double MinBoxWidth = 24;
    private const double MinBoxHeight = 14;

    private System.Guid? _resizingShapeId;
    private BoxHandle _resizeHandle;
    private (double Dx, double Dy) _resizeDeltaPage;

    /// <summary>If <paramref name="pt"/> lands on a corner handle of the currently-selected shape, which one.</summary>
    public (System.Guid Id, BoxHandle Handle)? TryHitResizeHandle(int pageIndex, PdfPoint pt)
    {
        if (!AnnotationsVisible || SelectedAnnotationId is not System.Guid sel)
        {
            return null;
        }

        PdfAnnotation? a = _document.GetAnnotations(pageIndex).FirstOrDefault(x => x.Id == sel);
        if (a is null || a.Kind is not (PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout or PdfAnnotationKind.Cloud))
        {
            return null;
        }

        double tol = 11 / Math.Max(0.05, SlotScale(Pages[pageIndex]));
        PdfRect b = a.Box;
        double l = Math.Min(b.Left, b.Right), r = Math.Max(b.Left, b.Right);
        double bt = Math.Min(b.Top, b.Bottom), tp = Math.Max(b.Top, b.Bottom);

        (double X, double Y, BoxHandle H)[] corners =
        [
            (l, tp, BoxHandle.TopLeft), (r, tp, BoxHandle.TopRight),
            (l, bt, BoxHandle.BottomLeft), (r, bt, BoxHandle.BottomRight),
        ];

        foreach ((double cx, double cy, BoxHandle h) in corners)
        {
            if (Math.Abs(pt.X - cx) <= tol && Math.Abs(pt.Y - cy) <= tol)
            {
                return (a.Id, h);
            }
        }

        return null;
    }

    public void BeginShapeResize(System.Guid id, BoxHandle handle)
    {
        _resizingShapeId = id;
        _resizeHandle = handle;
        _resizeDeltaPage = (0, 0);
        SelectedAnnotationId = id;
    }

    public void PreviewShapeResize(double dxPage, double dyPage)
    {
        if (_resizingShapeId is null)
        {
            return;
        }

        _resizeDeltaPage = (dxPage, dyPage);
        BuildAnnotationOverlays();
    }

    public void EndShapeResize(double dxPage, double dyPage)
    {
        System.Guid? id = _resizingShapeId;
        BoxHandle handle = _resizeHandle;
        _resizingShapeId = null;
        _resizeDeltaPage = (0, 0);
        if (id is null)
        {
            return;
        }

        (int page, PdfAnnotation? found) = FindAnnotation(id.Value);
        if (found is not { } a || (Math.Abs(dxPage) < 1 && Math.Abs(dyPage) < 1))
        {
            BuildAnnotationOverlays();
            return;
        }

        _document.UpdateAnnotation(page, ResizeShape(a, handle, dxPage, dyPage) with { Modified = System.DateTimeOffset.Now });
    }

    public void CancelShapeResize()
    {
        _resizingShapeId = null;
        _resizeDeltaPage = (0, 0);
        BuildAnnotationOverlays();
    }

    /// <summary>Move one corner of the shape's box by (dx, dy) page points, keeping a minimum size and
    /// (for a callout) re-attaching the leader. Sets <see cref="PdfAnnotation.AutoSize"/> off.</summary>
    private static PdfAnnotation ResizeShape(PdfAnnotation a, BoxHandle handle, double dx, double dy)
    {
        PdfRect b = a.Box;
        double l = Math.Min(b.Left, b.Right), r = Math.Max(b.Left, b.Right);
        double bt = Math.Min(b.Top, b.Bottom), tp = Math.Max(b.Top, b.Bottom);

        switch (handle)
        {
            case BoxHandle.TopLeft: l += dx; tp += dy; break;
            case BoxHandle.TopRight: r += dx; tp += dy; break;
            case BoxHandle.BottomLeft: l += dx; bt += dy; break;
            case BoxHandle.BottomRight: r += dx; bt += dy; break;
        }

        if (r - l < MinBoxWidth)
        {
            if (handle is BoxHandle.TopLeft or BoxHandle.BottomLeft)
            {
                l = r - MinBoxWidth;
            }
            else
            {
                r = l + MinBoxWidth;
            }
        }

        if (tp - bt < MinBoxHeight)
        {
            if (handle is BoxHandle.TopLeft or BoxHandle.TopRight)
            {
                tp = bt + MinBoxHeight;
            }
            else
            {
                bt = tp - MinBoxHeight;
            }
        }

        var box = new PdfRect(l, tp, r, bt);
        PdfAnnotation resized = a with { Quads = [box], AutoSize = false };
        if (a.Kind == PdfAnnotationKind.Callout && a.Leader.Count >= 1)
        {
            PdfPoint tip = a.Leader[0];
            resized = resized with { Strokes = [new[] { tip, BoxAttachPoint(box, tip) }] };
        }

        return resized;
    }

    // --- Moving a callout's arrow tip --------------------------------------------------

    private System.Guid? _leaderTipId;
    private (double Dx, double Dy) _leaderTipDeltaPage;

    /// <summary>If <paramref name="pt"/> lands on the selected callout's arrow-tip handle, its id.</summary>
    public System.Guid? TryHitLeaderTip(int pageIndex, PdfPoint pt)
    {
        if (!AnnotationsVisible || SelectedAnnotationId is not System.Guid sel)
        {
            return null;
        }

        PdfAnnotation? a = _document.GetAnnotations(pageIndex).FirstOrDefault(x => x.Id == sel);
        if (a is not { Kind: PdfAnnotationKind.Callout } || a.Leader.Count < 1)
        {
            return null;
        }

        double tol = 12 / Math.Max(0.05, SlotScale(Pages[pageIndex]));
        PdfPoint tip = a.Leader[0];
        return Math.Abs(pt.X - tip.X) <= tol && Math.Abs(pt.Y - tip.Y) <= tol ? a.Id : null;
    }

    public void BeginLeaderTipMove(System.Guid id)
    {
        _leaderTipId = id;
        _leaderTipDeltaPage = (0, 0);
        SelectedAnnotationId = id;
    }

    public void PreviewLeaderTipMove(double dxPage, double dyPage)
    {
        if (_leaderTipId is null)
        {
            return;
        }

        _leaderTipDeltaPage = (dxPage, dyPage);
        BuildAnnotationOverlays();
    }

    public void EndLeaderTipMove(double dxPage, double dyPage)
    {
        System.Guid? id = _leaderTipId;
        _leaderTipId = null;
        _leaderTipDeltaPage = (0, 0);
        if (id is null)
        {
            return;
        }

        (int page, PdfAnnotation? found) = FindAnnotation(id.Value);
        if (found is not { } a || (Math.Abs(dxPage) < 1 && Math.Abs(dyPage) < 1))
        {
            BuildAnnotationOverlays();
            return;
        }

        _document.UpdateAnnotation(page, MoveLeaderTip(a, dxPage, dyPage) with { Modified = System.DateTimeOffset.Now });
    }

    public void CancelLeaderTipMove()
    {
        _leaderTipId = null;
        _leaderTipDeltaPage = (0, 0);
        BuildAnnotationOverlays();
    }

    /// <summary>Move a callout's arrow tip by (dx, dy) page points; the box stays and the leader re-attaches.</summary>
    private static PdfAnnotation MoveLeaderTip(PdfAnnotation a, double dx, double dy)
    {
        if (a.Kind != PdfAnnotationKind.Callout || a.Leader.Count < 1)
        {
            return a;
        }

        var tip = new PdfPoint(a.Leader[0].X + dx, a.Leader[0].Y + dy);
        return a with { Strokes = [new[] { tip, BoxAttachPoint(a.Box, tip) }] };
    }

    public void CancelShape()
    {
        _shapePage = -1;
        ClearShapePreview();
    }

    private void UpdateShapePreview()
    {
        if (_shapePage < 0)
        {
            return;
        }

        PageSlotViewModel slot = Pages[_shapePage];
        double scale = SlotScale(slot);
        PdfSize unrotated = _document.GetUnrotatedPageSize(_shapePage);
        PdfRotation rotation = _document.GetPageRotation(_shapePage);
        PdfPoint crop = _document.GetCropOrigin(_shapePage);

        Point ToDip(PdfPoint p)
        {
            (double x, double y) = PdfCoordinates.PageToDevicePoint(p, unrotated, rotation, scale, crop);
            return new Point(x, y);
        }

        PdfRect pageRect = ShapeTool == ShapeTool.Callout
            ? DefaultBoxAt(_shapeHead, 190, 60)
            : new PdfRect(
                Math.Min(_shapeAnchor.X, _shapeHead.X), Math.Max(_shapeAnchor.Y, _shapeHead.Y),
                Math.Max(_shapeAnchor.X, _shapeHead.X), Math.Min(_shapeAnchor.Y, _shapeHead.Y));
        DeviceRect d = PdfCoordinates.PageToDevice(pageRect, unrotated, rotation, scale, crop);
        slot.ShapePreview = new Rect(d.X, d.Y, d.Width, d.Height);

        if (ShapeTool == ShapeTool.Callout)
        {
            Point tip = ToDip(_shapeAnchor);
            Point attach = ToDip(BoxAttachPoint(pageRect, _shapeAnchor));
            slot.ShapePreviewLeader = [tip, attach];
            slot.ShapePreviewArrow = LeaderArrowHead(tip, attach);
        }
        else
        {
            slot.ShapePreviewLeader = null;
            slot.ShapePreviewArrow = null;
        }
    }

    private void ClearShapePreview()
    {
        foreach (PageSlotViewModel slot in Pages)
        {
            if (slot.ShapePreview is not null)
            {
                slot.ShapePreview = null;
            }

            if (slot.ShapePreviewLeader is not null)
            {
                slot.ShapePreviewLeader = null;
                slot.ShapePreviewArrow = null;
            }
        }
    }

    /// <summary>An open arrowhead (base, tip, base) at <paramref name="tip"/>, aimed away from
    /// <paramref name="towards"/>, in DIP space.</summary>
    private static PointCollection LeaderArrowHead(Point tip, Point towards)
    {
        var dir = towards - tip;
        double len = dir.Length;
        if (len < 0.01)
        {
            return [];
        }

        dir /= len;
        var perp = new Vector(-dir.Y, dir.X);
        const double ah = 12, aw = 4.5;
        Point basePt = tip + (dir * ah);
        return [basePt + (perp * aw), tip, basePt - (perp * aw)];
    }

    private void UpdateSelectionRects()
    {
        if (_selectionPage < 0)
        {
            return;
        }

        int lo = Math.Min(_selectionAnchor, _selectionHead);
        int hi = Math.Max(_selectionAnchor, _selectionHead);
        PageSlotViewModel slot = Pages[_selectionPage];
        double scale = SlotScale(slot);
        PdfSize unrotated = _document.GetUnrotatedPageSize(_selectionPage);
        PdfRotation rotation = _document.GetPageRotation(_selectionPage);
        PdfPoint crop = _document.GetCropOrigin(_selectionPage);

        var rects = new List<Rect>();
        foreach (PdfRect quad in BuildLineQuads(_selectionPage, lo, hi))
        {
            DeviceRect d = PdfCoordinates.PageToDevice(quad, unrotated, rotation, scale, crop);
            rects.Add(new Rect(d.X, d.Y, d.Width, d.Height));
        }

        slot.SelectionRects = rects;
    }

    private IReadOnlyList<PdfCharBox> CharBoxes(int pageIndex)
    {
        if (!_charBoxCache.TryGetValue(pageIndex, out IReadOnlyList<PdfCharBox>? boxes))
        {
            boxes = PdfTextExtractor.GetCharBoxes(_document, pageIndex);
            _charBoxCache[pageIndex] = boxes;
        }

        return boxes;
    }

    private int NearestCharIndex(int pageIndex, PdfPoint pt)
    {
        IReadOnlyList<PdfCharBox> boxes = CharBoxes(pageIndex);
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < boxes.Count; i++)
        {
            PdfRect b = boxes[i].Box;
            double cx = (b.Left + b.Right) / 2;
            double cy = (b.Top + b.Bottom) / 2;
            if (pt.X >= b.Left && pt.X <= b.Right && pt.Y >= b.Bottom && pt.Y <= b.Top)
            {
                return i;
            }

            double dist = ((pt.X - cx) * (pt.X - cx)) + ((pt.Y - cy) * (pt.Y - cy));
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Merge the char boxes over [start, end] into one rectangle per text line.</summary>
    private IReadOnlyList<PdfRect> BuildLineQuads(int pageIndex, int start, int end)
    {
        IReadOnlyList<PdfCharBox> boxes = CharBoxes(pageIndex);
        if (boxes.Count == 0)
        {
            return [];
        }

        start = Math.Clamp(start, 0, boxes.Count - 1);
        end = Math.Clamp(end, 0, boxes.Count - 1);

        var quads = new List<PdfRect>();
        double left = 0, right = 0, top = 0, bottom = 0;
        bool inRun = false;

        void Flush()
        {
            if (inRun && right > left && top > bottom)
            {
                quads.Add(new PdfRect(left, top, right, bottom));
            }

            inRun = false;
        }

        for (int i = start; i <= end; i++)
        {
            PdfRect b = boxes[i].Box;

            // Skip degenerate boxes — spaces in particular come back with a real width but ~zero
            // height, and letting one through would flush the run (killing the vertical overlap
            // test) and split every word onto its own rectangle.
            if (b.Right - b.Left < 0.5 || b.Top - b.Bottom < 0.5)
            {
                continue;
            }

            // Same line while this glyph's vertical span still overlaps the run's. Sub/superscripts
            // and inline formula glyphs overlap and just widen the band; a real line break doesn't,
            // so it starts a fresh rectangle. Each line's quad is then the bounding box of its
            // glyphs — one clean rectangle, not a per-word staircase.
            if (inRun)
            {
                double overlap = Math.Min(top, b.Top) - Math.Max(bottom, b.Bottom);
                double glyphHeight = Math.Max(1, b.Top - b.Bottom);
                if (overlap < glyphHeight * 0.35)
                {
                    Flush();
                }
            }

            if (!inRun)
            {
                left = b.Left;
                right = b.Right;
                top = b.Top;
                bottom = b.Bottom;
                inRun = true;
            }
            else
            {
                left = Math.Min(left, b.Left);
                right = Math.Max(right, b.Right);
                top = Math.Max(top, b.Top);
                bottom = Math.Min(bottom, b.Bottom);
            }
        }

        Flush();
        return quads;
    }

    // --- Annotation commands ----------------------------------------------------------------

    [RelayCommand]
    private void CreateHighlight(string colorKey)
    {
        if (!HasPendingSelection)
        {
            return;
        }

        _document.AddAnnotation(PendingSelectionPage,
            PdfAnnotation.NewHighlight(_pendingQuads, AnnotationColors.ToArgb(colorKey), null, Author));
        ClearTextSelection();
    }

    [RelayCommand]
    private void CommentOnSelection()
    {
        if (!HasPendingSelection)
        {
            return;
        }

        int page = PendingSelectionPage;
        IReadOnlyList<PdfRect> quads = _pendingQuads;
        ClearTextSelection();

        OpenEditor(PdfAnnotationKind.Highlight, string.Empty, AnnotationColors.Default, canDelete: false,
            Author, System.DateTimeOffset.Now, null, result =>
        {
            if (result.Outcome == AnnotationEditorOutcome.Save)
            {
                _document.AddAnnotation(page, PdfAnnotation.NewHighlight(
                    quads, AnnotationColors.ToArgb(result.ColorKey),
                    string.IsNullOrWhiteSpace(result.Contents) ? null : result.Contents, Author)
                    with { Replies = result.Replies });
            }
        });
    }

    /// <summary>Drop a standalone note where the Comment tool was clicked.</summary>
    public void AddCommentAt(int pageIndex, PdfPoint pagePoint)
    {
        var iconRect = new PdfRect(pagePoint.X, pagePoint.Y + 18, pagePoint.X + 18, pagePoint.Y);
        CommentToolActive = false;

        OpenEditor(PdfAnnotationKind.Comment, string.Empty, null, canDelete: false,
            Author, System.DateTimeOffset.Now, null, result =>
        {
            if (result.Outcome == AnnotationEditorOutcome.Save)
            {
                _document.AddAnnotation(pageIndex, PdfAnnotation.NewComment(
                    iconRect, string.IsNullOrWhiteSpace(result.Contents) ? null : result.Contents, Author)
                    with { Replies = result.Replies });
            }
        });
    }

    [RelayCommand]
    private void EditAnnotation(System.Guid id)
    {
        (int page, PdfAnnotation? found) = FindAnnotation(id);
        if (found is not { } annotation)
        {
            return;
        }

        bool colored = annotation.Kind is PdfAnnotationKind.Highlight
            or PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout;
        string? colorKey = colored ? AnnotationColors.FromArgb(annotation.ColorArgb) : null;

        OpenEditor(annotation.Kind, annotation.Contents, colorKey, canDelete: true,
            annotation.Author, annotation.Created, annotation.Modified, result =>
        {
            switch (result.Outcome)
            {
                case AnnotationEditorOutcome.Save:
                    string? text = string.IsNullOrWhiteSpace(result.Contents) ? null : result.Contents;
                    bool isTextShape = annotation.Kind is PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout;
                    PdfAnnotation updated = annotation with
                    {
                        Contents = text,
                        ColorArgb = colored ? AnnotationColors.ToArgb(result.ColorKey) : annotation.ColorArgb,
                        FontSize = isTextShape ? result.FontSize : annotation.FontSize,
                        Replies = result.Replies,
                        Modified = System.DateTimeOffset.Now,
                    };

                    if (isTextShape)
                    {
                        PdfRect box = annotation.AutoSize
                            ? FitTextBox(annotation.Box, text, result.FontSize)
                            : GrowToFitText(annotation.Box, text, result.FontSize);
                        updated = updated with { Quads = [box] };
                        if (annotation.Kind == PdfAnnotationKind.Callout && annotation.Leader.Count >= 1)
                        {
                            PdfPoint tip = annotation.Leader[0];
                            updated = updated with { Strokes = [new[] { tip, BoxAttachPoint(box, tip) }] };
                        }
                    }

                    _document.UpdateAnnotation(page, updated);
                    break;
                case AnnotationEditorOutcome.Delete:
                    _document.RemoveAnnotation(page, id);
                    break;
            }
        }, annotation.Replies, annotation.FontSize);
    }

    [RelayCommand]
    private void DeleteAnnotation(System.Guid id)
    {
        (int page, PdfAnnotation? found) = FindAnnotation(id);
        if (found is not null)
        {
            if (SelectedAnnotationId == id)
            {
                SelectedAnnotationId = null;
            }

            _document.RemoveAnnotation(page, id);
        }
    }

    private (int Page, PdfAnnotation? Annotation) FindAnnotation(System.Guid id)
    {
        for (int page = 0; page < _document.PageCount; page++)
        {
            foreach (PdfAnnotation annotation in _document.GetAnnotations(page))
            {
                if (annotation.Id == id)
                {
                    return (page, annotation);
                }
            }
        }

        return (-1, null);
    }

    private static void OpenEditor(
        PdfAnnotationKind kind, string? contents, string? colorKey, bool canDelete,
        string? author, System.DateTimeOffset? created, System.DateTimeOffset? modified,
        Action<AnnotationEditorResult> onClosed, IReadOnlyList<PdfReply>? replies = null, double fontSize = 0)
    {
        // Defer past the current input event: a modal ShowDialog raised directly from a
        // mouse-down / popup-click handler opens without activating.
        Application.Current?.Dispatcher.BeginInvoke(
            () =>
            {
                var viewModel = new AnnotationEditorViewModel(
                    kind, contents, colorKey, canDelete, author, created, modified, replies, Author, fontSize);
                var window = new AnnotationEditorWindow(viewModel) { Owner = Application.Current?.MainWindow };
                AnnotationEditorResult? result = null;
                viewModel.Closed = r =>
                {
                    result = r;
                    window.Close();
                };

                window.ShowDialog();
                if (result is not null && result.Outcome != AnnotationEditorOutcome.Cancel)
                {
                    onClosed(result);
                }
            },
            DispatcherPriority.Input);
    }

    // --- Rendering ----------------------------------------------------------------------

    private void ApplyLayout()
    {
        if (Mode == ViewMode.Grid)
        {
            ApplyGridLayout();
        }
        else if (Mode == ViewMode.TwoPage)
        {
            ApplyTwoPageLayout();
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

        BuildAnnotationOverlays();
        BuildLinkOverlays();
    }

    /// <summary>Read mode: size the two pages of the current spread to fill the viewport.</summary>
    private void ApplyTwoPageLayout()
    {
        if (PageCount == 0)
        {
            return;
        }

        int leftIndex = SpreadLeftIndex;
        int rightIndex = Math.Min(PageCount - 1, leftIndex + 1);

        // Fit the widest page of the pair; both pages then share that scale so the spread
        // is centred and the shorter page isn't stretched.
        double pairWidthPoints = _pageSizes[leftIndex].Width + _pageSizes[rightIndex].Width;
        double maxHeightPoints = Math.Max(_pageSizes[leftIndex].Height, _pageSizes[rightIndex].Height);
        if (pairWidthPoints <= 0 || maxHeightPoints <= 0)
        {
            return;
        }

        double availableWidth = (_viewportWidth > 0 ? _viewportWidth : 1200) - FitMargin - PageSpacing;
        double availableHeight = (_viewportHeight > 0 ? _viewportHeight : 900) - FitMargin;
        double scale = Math.Min(availableWidth / pairWidthPoints, availableHeight / maxHeightPoints);
        scale = Math.Clamp(scale, MinZoom * PdfCoordinates.PointToDip, MaxZoom * PdfCoordinates.PointToDip);

        foreach (PageSlotViewModel slot in Pages)
        {
            slot.LayoutWidth = slot.SizePoints.Width * scale;
            slot.LayoutHeight = slot.SizePoints.Height * scale;
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

        BuildAnnotationOverlays();
        BuildLinkOverlays();
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
        if (Mode == ViewMode.TwoPage)
        {
            for (int i = SpreadLeftIndex; i <= SpreadLeftIndex + 1 && i < Pages.Count; i++)
            {
                Pages[i].IsRealized = true;
                RequestRender(Pages[i]);
            }

            return;
        }

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
        _rerenderTimer?.Stop();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
    }
}
