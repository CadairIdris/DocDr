using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.Pdf;
using Microsoft.Win32;

namespace DocDr.App.ViewModels;

public enum CompareViewMode
{
    Overlay,
    Old,
    New,
    Blend,
}

/// <summary>One row of the page overview.</summary>
public sealed class ComparePageItemViewModel(PageCompareSummary summary, bool isPending = false)
{
    public PageCompareSummary Summary { get; } = summary;

    /// <summary>A placeholder row: the overview scan hasn't reached this pair yet.</summary>
    public bool IsPending { get; } = isPending;

    public string Label => Summary switch
    {
        { OldPage: int o, NewPage: int n } when o == n => $"Page {o + 1}",
        { OldPage: int o, NewPage: int n } => $"Page {o + 1} ↔ {n + 1}",
        { OldPage: int o } => $"Page {o + 1}",
        { NewPage: int n } => $"Page {n + 1} (new)",
        _ => "",
    };

    public string StatusText => IsPending && Summary.Status == PageCompareStatus.Same ? "…" : Summary.Status switch
    {
        PageCompareStatus.Same => "no change",
        PageCompareStatus.Changed => Summary.Regions == 1 ? "1 change" : $"{Summary.Regions} changes",
        PageCompareStatus.OnlyInOld => "removed page",
        PageCompareStatus.OnlyInNew => "added page",
        _ => "",
    };

    public bool IsChanged => !IsPending && Summary.Status != PageCompareStatus.Same;

    public string AutomationName => $"{Label}, {StatusText}";
}

/// <summary>A change region on the comparison image, in DIPs at 100 % (image pixels × 96 / DPI).</summary>
public sealed partial class CompareRegionViewModel(double x, double y, double width, double height, int removed, int added) : ObservableObject
{
    public double X { get; } = x;

    public double Y { get; } = y;

    public double Width { get; } = width;

    public double Height { get; } = height;

    public int Removed { get; } = removed;

    public int Added { get; } = added;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// The visual compare window: an overview of which pages differ (computed in the background at low
/// resolution), and for the selected pair a full-resolution overlay — red = only in the old version,
/// blue = only in the new — or either version alone, or a blend slider between them.
/// The old document may be borrowed from an open tab (never disposed here); documents this window
/// loaded itself are disposed when it closes.
/// </summary>
public sealed partial class VisualCompareViewModel : ObservableObject, IDisposable
{
    private PdfDocument _oldDoc;
    private PdfDocument _newDoc;
    private string _oldName;
    private string _newName;
    private readonly List<PdfDocument> _owned = [];
    private CancellationTokenSource? _overviewCts;
    private CancellationTokenSource? _pageCts;

    public VisualCompareViewModel(PdfDocument oldDoc, string oldName, bool ownsOld, PdfDocument newDoc, string newName, bool ownsNew)
    {
        _oldDoc = oldDoc;
        _newDoc = newDoc;
        _oldName = oldName;
        _newName = newName;
        if (ownsOld)
        {
            _owned.Add(oldDoc);
        }

        if (ownsNew)
        {
            _owned.Add(newDoc);
        }
    }

    /// <summary>Raised when a box on the image (DIPs at 100 %) should be scrolled into view.</summary>
    public event Action<double, double, double, double>? ScrollToRequested;

    /// <summary>Raised after a new comparison image arrives (the view refits on the first one).</summary>
    public event Action? ImageChanged;

    public string OldName => _oldName;

    public string NewName => _newName;

    public bool Uses(PdfDocument document) => ReferenceEquals(document, _oldDoc) || ReferenceEquals(document, _newDoc);

    public ObservableCollection<ComparePageItemViewModel> Pages { get; } = [];

    public ObservableCollection<CompareRegionViewModel> Regions { get; } = [];

    [ObservableProperty]
    private ComparePageItemViewModel? _selectedPage;

    [ObservableProperty]
    private int _newPageOffset;

    [ObservableProperty]
    private bool _ignoreSmallShifts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlay), nameof(ShowOld), nameof(ShowNew), nameof(IsBlend), nameof(NewOpacity))]
    private CompareViewMode _viewMode = CompareViewMode.Overlay;

    /// <summary>Blend position: 0 = old only, 1 = new only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewOpacity))]
    private double _blend = 0.5;

    [ObservableProperty]
    private double _zoom = 1;

    [ObservableProperty]
    private ImageSource? _overlayImage;

    [ObservableProperty]
    private ImageSource? _oldImage;

    [ObservableProperty]
    private ImageSource? _newImage;

    [ObservableProperty]
    private double _imageWidth;

    [ObservableProperty]
    private double _imageHeight;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private double _scanProgress;

    [ObservableProperty]
    private string _overviewText = "";

    [ObservableProperty]
    private bool _isComparing;

    [ObservableProperty]
    private string _pageText = "";

    [ObservableProperty]
    private CompareRegionViewModel? _selectedRegion;

    public bool IsOverlay => ViewMode == CompareViewMode.Overlay;

    public bool IsBlend => ViewMode == CompareViewMode.Blend;

    public bool ShowOld => ViewMode is CompareViewMode.Old or CompareViewMode.Blend;

    public bool ShowNew => ViewMode is CompareViewMode.New or CompareViewMode.Blend;

    /// <summary>The new image sits on top of the old one; in blend mode its opacity is the slider.</summary>
    public double NewOpacity => ViewMode == CompareViewMode.Blend ? Blend : 1;

    public string RegionPosition => SelectedRegion is null || Regions.Count == 0
        ? (Regions.Count == 0 ? "" : $"{Regions.Count} changes")
        : $"Change {Regions.IndexOf(SelectedRegion) + 1} of {Regions.Count}";

    public void Start() => RunOverview();

    // --- Overview -----------------------------------------------------------------------------

    private void RunOverview()
    {
        _overviewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _overviewCts = cts;

        int? keepOld = SelectedPage?.Summary.OldPage ?? 0;
        Pages.Clear();
        PdfDocument oldDoc = _oldDoc, newDoc = _newDoc;
        int offset = NewPageOffset;
        int total = PdfVisualCompare.PairCount(oldDoc.PageCount, newDoc.PageCount, offset);
        IsScanning = true;
        ScanProgress = 0;
        OverviewText = "Comparing pages…";

        // Placeholder rows so a page can be opened before the scan reaches it.
        int first = Math.Min(0, -offset);
        for (int i = first; i < first + total; i++)
        {
            int j = i + offset;
            bool hasOld = i >= 0 && i < oldDoc.PageCount, hasNew = j >= 0 && j < newDoc.PageCount;
            var status = hasOld && hasNew ? PageCompareStatus.Same : hasOld ? PageCompareStatus.OnlyInOld : PageCompareStatus.OnlyInNew;
            Pages.Add(new ComparePageItemViewModel(new PageCompareSummary(hasOld ? i : null, hasNew ? j : null, status, 0), isPending: true));
        }

        SelectedPage = Pages.FirstOrDefault(p => p.Summary.OldPage == keepOld) ?? Pages.FirstOrDefault();

        var progress = new Progress<int>(done =>
        {
            if (!cts.IsCancellationRequested)
            {
                ScanProgress = total == 0 ? 1 : (double)done / total;
            }
        });

        Task.Run(() => PdfVisualCompare.CompareDocuments(oldDoc, newDoc, offset, 40, progress, cts.Token), cts.Token)
            .ContinueWith(t =>
            {
                _ = t.Exception; // observed, so a cancelled run's failure isn't logged as unobserved
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                IsScanning = false;
                if (t.IsFaulted)
                {
                    OverviewText = "Couldn't compare: " + t.Exception?.GetBaseException().Message;
                    return;
                }

                int? selected = SelectedPage?.Summary.OldPage;
                int? selectedNew = SelectedPage?.Summary.NewPage;
                Pages.Clear();
                foreach (PageCompareSummary s in t.Result)
                {
                    Pages.Add(new ComparePageItemViewModel(s));
                }

                _suppressPageCompare = true;
                SelectedPage = Pages.FirstOrDefault(p => p.Summary.OldPage == selected && p.Summary.NewPage == selectedNew)
                    ?? Pages.FirstOrDefault();
                _suppressPageCompare = false;

                int changed = t.Result.Count(s => s.Status == PageCompareStatus.Changed);
                int onlyOld = t.Result.Count(s => s.Status == PageCompareStatus.OnlyInOld);
                int onlyNew = t.Result.Count(s => s.Status == PageCompareStatus.OnlyInNew);
                var parts = new List<string> { changed == 1 ? "1 page changed" : $"{changed} pages changed" };
                if (onlyOld > 0)
                {
                    parts.Add($"{onlyOld} only in old");
                }

                if (onlyNew > 0)
                {
                    parts.Add($"{onlyNew} only in new");
                }

                OverviewText = string.Join(" · ", parts);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private bool _suppressPageCompare;

    partial void OnNewPageOffsetChanged(int value) => RunOverview();

    partial void OnIgnoreSmallShiftsChanged(bool value) => ComparePage();

    partial void OnSelectedPageChanged(ComparePageItemViewModel? value)
    {
        if (!_suppressPageCompare)
        {
            ComparePage();
        }
    }

    [RelayCommand]
    private void IncreaseOffset() => NewPageOffset++;

    [RelayCommand]
    private void DecreaseOffset() => NewPageOffset--;

    [RelayCommand]
    private void Swap()
    {
        (_oldDoc, _newDoc) = (_newDoc, _oldDoc);
        (_oldName, _newName) = (_newName, _oldName);
        OnPropertyChanged(nameof(OldName));
        OnPropertyChanged(nameof(NewName));
        InvalidateTextResults();
        if (NewPageOffset != 0)
        {
            NewPageOffset = -NewPageOffset; // re-runs the overview
        }
        else
        {
            RunOverview();
        }
    }

    // --- One page -----------------------------------------------------------------------------

    private void ComparePage()
    {
        // Null while the page list is being rebuilt (the ListBox drops its selection) — keep any
        // comparison in flight; the rebuilt list re-selects the same pair without re-comparing.
        if (SelectedPage is { } item)
        {
            ShowPair(item.Summary.OldPage, item.Summary.NewPage, null);
        }
    }

    /// <summary>Compare old page <paramref name="oldPage"/> with new page <paramref name="newPage"/> (either
    /// may be null: that page is shown alone), outlining <paramref name="highlight"/>'s words if given.</summary>
    private void ShowPair(int? oldPage, int? newPage, TextHighlight? highlight)
    {
        if (oldPage is null && newPage is null)
        {
            return;
        }

        _pageCts?.Cancel();

        var cts = new CancellationTokenSource();
        _pageCts = cts;
        PdfDocument oldDoc = _oldDoc, newDoc = _newDoc;
        var options = new VisualCompareOptions { TolerancePixels = IgnoreSmallShifts ? 1 : 0 };
        IsComparing = true;
        PageText = "Comparing…";

        Task.Run(() =>
        {
            if (oldPage is int o && newPage is int n)
            {
                return (Diff: PdfVisualCompare.ComparePages(oldDoc, o, newDoc, n, options, cts.Token), Single: (RenderedPage?)null, Dpi: 0.0);
            }

            // A page only one document has: show it on its own.
            PdfDocument doc = oldPage is not null ? oldDoc : newDoc;
            int page = oldPage ?? newPage!.Value;
            PdfSize size = doc.GetPageSize(page);
            double scale = Math.Min(options.Dpi / PdfCoordinates.PointsPerInch,
                Math.Sqrt(options.MaxPixels / Math.Max(1, size.Width * size.Height)));
            int w = Math.Max(1, (int)Math.Ceiling(size.Width * scale)), h = Math.Max(1, (int)Math.Ceiling(size.Height * scale));
            return (Diff: (VisualDiff?)null, Single: new PageRenderer().Render(doc, page, w, h, cts.Token), Dpi: scale * PdfCoordinates.PointsPerInch);
        }, cts.Token).ContinueWith(t =>
        {
            _ = t.Exception;
            if (cts.IsCancellationRequested)
            {
                return;
            }

            IsComparing = false;
            Regions.Clear();
            Highlights.Clear();
            SelectedRegion = null;
            if (t.IsFaulted)
            {
                PageText = "Couldn't compare: " + t.Exception?.GetBaseException().Message;
                return;
            }

            (VisualDiff? diff, RenderedPage? single, double singleDpi) = t.Result;
            if (diff is not null)
            {
                OverlayImage = Bitmap(diff.Width, diff.Height, diff.Stride, diff.Overlay, diff.Dpi);
                OldImage = Bitmap(diff.Width, diff.Height, diff.Stride, diff.Old, diff.Dpi);
                NewImage = Bitmap(diff.Width, diff.Height, diff.Stride, diff.New, diff.Dpi);
                ImageWidth = diff.Width * 96 / diff.Dpi;
                ImageHeight = diff.Height * 96 / diff.Dpi;
                double k = 96 / diff.Dpi;
                foreach (VisualDiffRegion r in diff.Regions)
                {
                    Regions.Add(new CompareRegionViewModel(r.X * k, r.Y * k, r.Width * k, r.Height * k, r.RemovedPixels, r.AddedPixels));
                }

                PageText = Describe(diff);
                if (highlight is not null)
                {
                    AddHighlights(diff, oldPage, newPage, highlight, k);
                }
            }
            else if (single is not null)
            {
                BitmapSource image = Bitmap(single.PixelWidth, single.PixelHeight, single.Stride, single.Pixels, singleDpi);
                OverlayImage = OldImage = NewImage = image;
                ImageWidth = single.PixelWidth * 96 / singleDpi;
                ImageHeight = single.PixelHeight * 96 / singleDpi;
                PageText = oldPage is not null
                    ? "This page is only in the old document."
                    : "This page is only in the new document.";
            }

            OnPropertyChanged(nameof(RegionPosition));
            ImageChanged?.Invoke();
            if (Highlights.Count > 0)
            {
                CompareHighlightViewModel h0 = Highlights[0];
                ScrollToRequested?.Invoke(h0.X, h0.Y, h0.Width, h0.Height);
            }
            else if (Regions.Count > 0)
            {
                SelectRegion(Regions[0]);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static string Describe(VisualDiff d)
    {
        var parts = new List<string>
        {
            d.Regions.Count switch { 0 => "No differences", 1 => "1 change", int n => $"{n} changes" },
        };
        if (d.OffsetX != 0 || d.OffsetY != 0)
        {
            double k = PdfCoordinates.PointsPerInch / d.Dpi;
            parts.Add($"new page aligned by {d.OffsetX * k:0.#}, {d.OffsetY * k:0.#} pt");
        }

        if (d.SizesDiffer)
        {
            parts.Add("pages are different sizes — the new one was scaled to fit");
        }

        return string.Join(" · ", parts);
    }

    private static BitmapSource Bitmap(int w, int h, int stride, byte[] pixels, double dpi)
    {
        BitmapSource b = BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Bgr32, null, pixels, stride);
        b.Freeze();
        return b;
    }

    // --- Change navigation --------------------------------------------------------------------

    partial void OnSelectedRegionChanged(CompareRegionViewModel? oldValue, CompareRegionViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }

        OnPropertyChanged(nameof(RegionPosition));
    }

    public void SelectRegion(CompareRegionViewModel region)
    {
        SelectedRegion = region;
        ScrollToRequested?.Invoke(region.X, region.Y, region.Width, region.Height);
    }

    [RelayCommand]
    private void NextChange() => StepChange(+1);

    [RelayCommand]
    private void PreviousChange() => StepChange(-1);

    private void StepChange(int direction)
    {
        if (Regions.Count > 0)
        {
            int i = SelectedRegion is null ? (direction > 0 ? 0 : Regions.Count - 1)
                : Regions.IndexOf(SelectedRegion) + direction;
            if (i >= 0 && i < Regions.Count)
            {
                SelectRegion(Regions[i]);
                return;
            }
        }

        // Past the last change on this page: go to the next / previous changed page.
        int at = SelectedPage is null ? -1 : Pages.IndexOf(SelectedPage);
        for (int p = at + direction; p >= 0 && p < Pages.Count; p += direction)
        {
            if (Pages[p].IsChanged)
            {
                SelectedPage = Pages[p];
                return;
            }
        }
    }

    [RelayCommand]
    private void SetViewMode(CompareViewMode mode) => ViewMode = mode;

    // --- Save ---------------------------------------------------------------------------------

    [RelayCommand]
    private void SaveImage()
    {
        if (OverlayImage is not BitmapSource image)
        {
            return;
        }

        string stem = Path.GetFileNameWithoutExtension(_newName);
        int page = (SelectedPage?.Summary.NewPage ?? SelectedPage?.Summary.OldPage ?? 0) + 1;
        var dialog = new SaveFileDialog
        {
            Title = "Save comparison image",
            Filter = "PNG image (*.png)|*.png",
            FileName = $"{stem} compare p{page}.png",
            InitialDirectory = _newDoc.FilePath is { } path ? Path.GetDirectoryName(path) : null,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using FileStream stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Save comparison image", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void Dispose()
    {
        _overviewCts?.Cancel();
        _pageCts?.Cancel();
        _textCts?.Cancel();
        _clauseCts?.Cancel();

        // Let any in-flight comparison finish with its document before disposing (it's cancelled,
        // so it stops at the next page) — PdfDocument serialises access, Dispose takes the same lock.
        foreach (PdfDocument doc in _owned)
        {
            doc.Dispose();
        }

        _owned.Clear();
    }
}
