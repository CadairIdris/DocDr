using System.Collections.Generic;
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
    }

    public string Title { get; }

    public string FilePath => Document.FilePath;

    public PdfDocument Document { get; }

    public PdfPaneViewModel LeftPane { get; }

    public PdfPaneViewModel RightPane { get; }

    /// <summary>Raised when the tab's own close affordance is used.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        LeftPane.Dispose();
        RightPane.Dispose();
        _cache.Purge(Document);
        Document.Dispose();
    }
}
