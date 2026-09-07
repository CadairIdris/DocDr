using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.App.Views;
using DocDr.Pdf;
using Microsoft.Win32;

namespace DocDr.App.ViewModels;

/// <summary>
/// One workspace tab: a single open <see cref="PdfDocument"/> shown through two independent panes,
/// a thumbnail strip and a bookmark tree. Also owns the page-editing commands and rebuilds the
/// child view models whenever the document's pages change.
/// </summary>
public sealed partial class DocumentTabViewModel : ObservableObject, IDisposable
{
    private readonly CachingPageRenderer _cache;
    private string _baseTitle;

    public DocumentTabViewModel(
        string title,
        PdfDocument document,
        IReadOnlyList<PdfSize> pageSizes,
        IReadOnlyList<PdfBookmark> bookmarks,
        BackgroundRenderQueue queue,
        CachingPageRenderer cache)
    {
        _baseTitle = title;
        Document = document;
        _cache = cache;

        LeftPane = new PdfPaneViewModel("Left", document, pageSizes, queue);
        RightPane = new PdfPaneViewModel("Right", document, pageSizes, queue);

        Thumbnails = new ThumbnailStripViewModel(document, pageSizes, queue);
        Thumbnails.PageActivated += pageIndex => LeftPane.GoToPage(pageIndex + 1);
        Thumbnails.EditRequested += OnThumbnailEditRequested;

        Bookmarks = new BookmarksViewModel(bookmarks);
        Bookmarks.BookmarkActivated += pageIndex => LeftPane.GoToPage(pageIndex + 1);

        Clauses = new ClausesViewModel();
        Clauses.ClauseActivated += pageIndex => LeftPane.GoToPage(pageIndex + 1);
        Clauses.Load(document);

        Annotations = new AnnotationListViewModel();
        Annotations.Reload(document);
        Annotations.AnnotationActivated += OnAnnotationActivated;

        LeftPane.PropertyChanged += OnLeftPanePropertyChanged;
        RightPane.PropertyChanged += OnRightPanePropertyChanged;
        Document.Changed += OnDocumentChanged;
        Document.DirtyChanged += OnDocumentDirtyChanged;
        Document.AnnotationsChanged += OnAnnotationsChanged;

        Thumbnails.SetCurrentPage(LeftPane.CurrentPage);
    }

    public PdfDocument Document { get; }

    public PdfPaneViewModel LeftPane { get; }

    public PdfPaneViewModel RightPane { get; }

    public ThumbnailStripViewModel Thumbnails { get; }

    public BookmarksViewModel Bookmarks { get; }

    public ClausesViewModel Clauses { get; }

    public AnnotationListViewModel Annotations { get; }

    public string Title => Document.IsDirty ? $"{_baseTitle} •" : _baseTitle;

    public string? FilePath => Document.FilePath;

    public bool CanUndo => Document.CanUndo;

    public bool CanRedo => Document.CanRedo;

    [ObservableProperty]
    private bool _isSplitView;

    partial void OnIsSplitViewChanged(bool value)
    {
        // Open the second pane on whatever page the first one is showing; it stays independent after.
        if (value)
        {
            RightPane.GoToPage(LeftPane.CurrentPage);
        }
    }

    [ObservableProperty]
    private bool _isNavigationPanelVisible;

    [ObservableProperty]
    private NavigationTab _navigationTab = NavigationTab.Pages;

    /// <summary>When on, clicking a page drops a standalone comment. Mirrored to both panes.</summary>
    [ObservableProperty]
    private bool _commentToolActive;

    partial void OnCommentToolActiveChanged(bool value)
    {
        LeftPane.CommentToolActive = value;
        RightPane.CommentToolActive = value;
        if (value)
        {
            HighlighterToolActive = false;
            ShapeTool = ShapeTool.None;
            AnnotationsVisible = true;
        }
    }

    /// <summary>The armed "drop a shape" tool (text box / callout / cloud). Mirrored to both panes.</summary>
    [ObservableProperty]
    private ShapeTool _shapeTool;

    partial void OnShapeToolChanged(ShapeTool value)
    {
        LeftPane.ShapeTool = value;
        RightPane.ShapeTool = value;
        if (value != ShapeTool.None)
        {
            CommentToolActive = false;
            HighlighterToolActive = false;
            AnnotationsVisible = true;
        }
    }

    /// <summary>Global show/hide for the annotation overlay (highlights, notes, ink). View-only.</summary>
    [ObservableProperty]
    private bool _annotationsVisible = true;

    partial void OnAnnotationsVisibleChanged(bool value)
    {
        LeftPane.AnnotationsVisible = value;
        RightPane.AnnotationsVisible = value;
        OnPropertyChanged(nameof(MarkupButtonLabel));
    }

    public string MarkupButtonLabel => AnnotationsVisible ? "👁 Markup" : "👁 Markup hidden";

    [RelayCommand]
    private void ToggleAnnotations() => AnnotationsVisible = !AnnotationsVisible;

    /// <summary>When on, dragging on a page draws a freehand highlighter stroke. Mirrored to both panes.</summary>
    [ObservableProperty]
    private bool _highlighterToolActive;

    partial void OnHighlighterToolActiveChanged(bool value)
    {
        LeftPane.HighlighterToolActive = value;
        RightPane.HighlighterToolActive = value;
        if (value)
        {
            CommentToolActive = false;
            ShapeTool = ShapeTool.None;
            AnnotationsVisible = true;
        }
    }

    /// <summary>Palette key the highlighter pen uses, mirrored to both panes.</summary>
    [ObservableProperty]
    private string _inkColorKey = AnnotationColors.Default;

    partial void OnInkColorKeyChanged(string value)
    {
        LeftPane.InkColorKey = value;
        RightPane.InkColorKey = value;
    }

    /// <summary>The highlighter palette for the toolbar swatches.</summary>
    public System.Collections.Generic.IReadOnlyList<string> InkColorKeys => AnnotationColors.Keys;

    /// <summary>Whether the Annotations navigation tab is offered (the document has any).</summary>
    public bool HasAnnotations => Document.HasAnnotations;

    /// <summary>Raised when the tab's own close affordance is used.</summary>
    public event EventHandler? CloseRequested;

    // --- Navigation panel ----------------------------------------------------------------

    /// <summary>Show or hide the side panel (its section is chosen from the panel's own tab strip).</summary>
    [RelayCommand]
    private void ToggleNavigationPanel() => IsNavigationPanelVisible = !IsNavigationPanelVisible;

    // --- Page editing ------------------------------------------------------------------

    /// <summary>Pages an edit acts on: the thumbnail selection when the Pages panel is showing it,
    /// otherwise just the active pane's current page.</summary>
    private IReadOnlyList<int> TargetPages()
    {
        if (IsNavigationPanelVisible && NavigationTab == NavigationTab.Pages && Thumbnails.HasSelection)
        {
            return Thumbnails.SelectedPageIndices;
        }

        return LeftPane.CurrentPage >= 1 ? [LeftPane.CurrentPage - 1] : [];
    }

    [RelayCommand]
    private void RotateRight() => Rotate(PdfRotation.Clockwise90);

    [RelayCommand]
    private void RotateLeft() => Rotate(PdfRotation.CounterClockwise90);

    [RelayCommand]
    private void Rotate180() => Rotate(PdfRotation.Rotate180);

    private void Rotate(PdfRotation delta)
    {
        IReadOnlyList<int> pages = TargetPages();
        if (pages.Count > 0)
        {
            Document.RotatePages(pages, delta);
        }
    }

    [RelayCommand]
    private void DeletePages()
    {
        IReadOnlyList<int> pages = TargetPages();
        if (pages.Count == 0)
        {
            return;
        }

        if (pages.Count >= Document.PageCount)
        {
            MessageBox.Show("A document must keep at least one page.", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string label = pages.Count == 1 ? $"page {pages[0] + 1}" : $"{pages.Count} pages";
        if (MessageBox.Show($"Delete {label}?", "DocDr", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) == MessageBoxResult.OK)
        {
            Document.DeletePages(pages);
        }
    }

    [RelayCommand]
    private void InsertPdf()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Insert pages from PDF",
            Filter = "PDF documents (*.pdf)|*.pdf",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            int at = TargetPages() is [var first, ..] ? first + 1 : LeftPane.CurrentPage;
            Document.InsertPages(dialog.FileName, at);
        }
        catch (Exception ex) when (ex is PdfException or IOException)
        {
            MessageBox.Show($"Could not insert that PDF: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void InsertBlankPage()
    {
        int after = TargetPages() is [var first, ..] ? first : LeftPane.CurrentPage - 1;
        Document.InsertBlankPage(after);
        LeftPane.GoToPage(after + 2);
    }

    [RelayCommand]
    private void Print()
    {
        try
        {
            new PrintService().Print(Document, _baseTitle);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not print: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ShowProperties()
    {
        var dialog = new DocumentPropertiesWindow(new DocumentPropertiesViewModel(Document))
        {
            Owner = Application.Current?.MainWindow,
        };
        dialog.ShowDialog();
    }

    [RelayCommand]
    private void RemoveWatermarks()
    {
        IReadOnlyList<WatermarkCandidate> candidates;
        try
        {
            candidates = PdfWatermarks.Scan(Document);
        }
        catch (Exception ex) when (ex is PdfException)
        {
            MessageBox.Show($"Could not scan for watermarks: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (candidates.Count == 0)
        {
            MessageBox.Show(
                "No content repeated across the pages was detected — nothing to remove.",
                "DocDr", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Defer past the current click so the modal window activates (see the CLAUDE.md note
        // on ShowDialog from an input handler).
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var viewModel = new WatermarkReviewViewModel(Document, candidates);
            var dialog = new WatermarkReviewWindow(viewModel) { Owner = Application.Current?.MainWindow };
            IReadOnlyList<WatermarkCandidate>? chosen = null;
            viewModel.Closed = result =>
            {
                chosen = result;
                dialog.Close();
            };

            dialog.ShowDialog();
            if (chosen is { Count: > 0 })
            {
                try
                {
                    Document.RemoveWatermarks(chosen);
                }
                catch (PdfException ex)
                {
                    MessageBox.Show($"Could not remove the watermarks: {ex.Message}", "DocDr",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => Document.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => Document.Redo();

    private void OnThumbnailEditRequested(ThumbnailStripViewModel.ThumbnailCommand command)
    {
        switch (command)
        {
            case ThumbnailStripViewModel.ThumbnailCommand.RotateRight: RotateRight(); break;
            case ThumbnailStripViewModel.ThumbnailCommand.RotateLeft: RotateLeft(); break;
            case ThumbnailStripViewModel.ThumbnailCommand.Rotate180: Rotate180(); break;
            case ThumbnailStripViewModel.ThumbnailCommand.Delete: DeletePages(); break;
            case ThumbnailStripViewModel.ThumbnailCommand.InsertAfter: InsertPdf(); break;
            case ThumbnailStripViewModel.ThumbnailCommand.InsertBlankAfter: InsertBlankPage(); break;
        }
    }

    // --- Saving --------------------------------------------------------------------------

    [RelayCommand]
    private void Save()
    {
        if (Document.FilePath is null)
        {
            SaveAs();
            return;
        }

        try
        {
            Document.Save();
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void SaveAs()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save PDF as",
            Filter = "PDF documents (*.pdf)|*.pdf",
            FileName = Document.FilePath is { } p ? Path.GetFileName(p) : _baseTitle,
            AddExtension = true,
            DefaultExt = ".pdf",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            Document.SaveAs(dialog.FileName);
            _baseTitle = Path.GetFileName(dialog.FileName);
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(FilePath));
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Prompt to save a dirty document. Returns false to cancel the pending close.</summary>
    public bool ConfirmClose()
    {
        if (!Document.IsDirty)
        {
            return true;
        }

        MessageBoxResult choice = MessageBox.Show(
            $"Save changes to {_baseTitle}?", "DocDr",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        return choice switch
        {
            MessageBoxResult.Yes => SaveAndConfirm(),
            MessageBoxResult.No => true,
            _ => false,
        };

        bool SaveAndConfirm()
        {
            Save();
            return !Document.IsDirty;
        }
    }

    // --- Reacting to document changes -------------------------------------------------

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        _cache.Purge(Document);
        IReadOnlyList<PdfSize> sizes = Document.GetPageSizes();

        LeftPane.ReloadPages(sizes);
        RightPane.ReloadPages(sizes);
        Thumbnails.Reload(sizes);
        Bookmarks.Reload(PdfBookmarks.Read(Document));
        Clauses.Load(Document);
        Annotations.Reload(Document);
        Thumbnails.SetCurrentPage(LeftPane.CurrentPage);

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasAnnotations));
        RaiseEditState();
    }

    private void OnAnnotationsChanged(object? sender, AnnotationsChangedEventArgs e)
    {
        LeftPane.BuildAnnotationOverlays();
        RightPane.BuildAnnotationOverlays();
        Annotations.Reload(Document);
        OnPropertyChanged(nameof(HasAnnotations));
    }

    private void OnAnnotationActivated(int pageIndex, Guid id)
    {
        LeftPane.SelectedAnnotationId = id;
        RightPane.SelectedAnnotationId = id;
        LeftPane.GoToPage(pageIndex + 1);
    }

    private void OnDocumentDirtyChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        RaiseEditState();
    }

    private void RaiseEditState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void OnLeftPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfPaneViewModel.CurrentPage))
        {
            Thumbnails.SetCurrentPage(LeftPane.CurrentPage);
        }
        else if (e.PropertyName == nameof(PdfPaneViewModel.CommentToolActive) && !LeftPane.CommentToolActive)
        {
            CommentToolActive = false;
        }
        else if (e.PropertyName == nameof(PdfPaneViewModel.ShapeTool) && LeftPane.ShapeTool == ShapeTool.None)
        {
            ShapeTool = ShapeTool.None;
        }
    }

    private void OnRightPanePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfPaneViewModel.CommentToolActive) && !RightPane.CommentToolActive)
        {
            CommentToolActive = false;
        }
        else if (e.PropertyName == nameof(PdfPaneViewModel.ShapeTool) && RightPane.ShapeTool == ShapeTool.None)
        {
            ShapeTool = ShapeTool.None;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        LeftPane.PropertyChanged -= OnLeftPanePropertyChanged;
        RightPane.PropertyChanged -= OnRightPanePropertyChanged;
        Document.Changed -= OnDocumentChanged;
        Document.DirtyChanged -= OnDocumentDirtyChanged;
        Document.AnnotationsChanged -= OnAnnotationsChanged;
        Thumbnails.EditRequested -= OnThumbnailEditRequested;
        Annotations.AnnotationActivated -= OnAnnotationActivated;
        Clauses.CancelLoad();
        LeftPane.Dispose();
        RightPane.Dispose();
        _cache.Purge(Document);
        Document.Dispose();
    }
}
