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
        IReadOnlyList<PdfBookmark> bookmarks,
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

        Bookmarks = new BookmarksViewModel(bookmarks);
        Bookmarks.BookmarkActivated += pageIndex => LeftPane.GoToPage(pageIndex + 1);

        LeftPane.PropertyChanged += OnLeftPanePropertyChanged;
        Thumbnails.SetCurrentPage(LeftPane.CurrentPage);
    }

    public string Title { get; }

    public string FilePath => Document.FilePath;

    /// <summary>Whether the second (right) pane is shown. Off by default — one pane per tab.</summary>
    [ObservableProperty]
    private bool _isSplitView;

    /// <summary>Whether the navigation panel (page thumbnails / bookmarks) is shown. Off by default.</summary>
    [ObservableProperty]
    private bool _isNavigationPanelVisible;

    /// <summary>Which navigation-panel section is showing.</summary>
    [ObservableProperty]
    private NavigationTab _navigationTab = NavigationTab.Pages;

    public PdfDocument Document { get; }

    public PdfPaneViewModel LeftPane { get; }

    public PdfPaneViewModel RightPane { get; }

    public ThumbnailStripViewModel Thumbnails { get; }

    public BookmarksViewModel Bookmarks { get; }

    /// <summary>Toolbar: open the navigation panel on <paramref name="tab"/>, or close it if that section is already showing.</summary>
    [RelayCommand]
    private void ToggleNavigation(NavigationTab tab)
    {
        if (IsNavigationPanelVisible && NavigationTab == tab)
        {
            IsNavigationPanelVisible = false;
        }
        else
        {
            NavigationTab = tab;
            IsNavigationPanelVisible = true;
        }
    }

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
