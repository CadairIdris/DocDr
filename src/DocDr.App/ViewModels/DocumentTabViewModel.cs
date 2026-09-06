using System.Collections.Generic;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>
/// One workspace tab: a single open <see cref="PdfDocument"/> shown through two independent panes.
/// A file opened in two tabs is loaded once per tab — no shared mutable handle across tabs.
/// </summary>
public sealed partial class DocumentTabViewModel : ObservableObject, IDisposable
{
    private readonly CachingPageRenderer _cache;

    public DocumentTabViewModel(
        string title,
        PdfDocument document,
        IReadOnlyList<PdfSize> pageSizes,
        BackgroundRenderQueue queue,
        CachingPageRenderer cache)
    {
        Title = title;
        Document = document;
        _cache = cache;

        LeftPane = new PdfPaneViewModel("Left", document, pageSizes, queue);
        RightPane = new PdfPaneViewModel("Right", document, pageSizes, queue);

        Thumbnails = new ThumbnailStripViewModel(document, pageSizes, queue);
        Thumbnails.PageActivated += pageIndex => LeftPane.GoToPage(pageIndex + 1);
        LeftPane.PropertyChanged += OnLeftPanePropertyChanged;
        Thumbnails.SetCurrentPage(LeftPane.CurrentPage);
    }

    public string Title { get; }

    public string FilePath => Document.FilePath;

    /// <summary>Whether the second (right) pane is shown. Off by default — one pane per tab.</summary>
    [ObservableProperty]
    private bool _isSplitView;

    /// <summary>Whether the thumbnail navigation strip is shown. Off by default.</summary>
    [ObservableProperty]
    private bool _isThumbnailStripVisible;

    public PdfDocument Document { get; }

    public PdfPaneViewModel LeftPane { get; }

    public PdfPaneViewModel RightPane { get; }

    public ThumbnailStripViewModel Thumbnails { get; }

    private void OnLeftPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfPaneViewModel.CurrentPage))
        {
            Thumbnails.SetCurrentPage(LeftPane.CurrentPage);
        }
    }

    /// <summary>Raised when the tab's own close affordance is used.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        LeftPane.PropertyChanged -= OnLeftPanePropertyChanged;
        LeftPane.Dispose();
        RightPane.Dispose();
        _cache.Purge(Document);
        Document.Dispose();
    }
}
