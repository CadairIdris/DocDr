using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.App.Views;
using DocDr.Ocr;
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

        Bookmarks = new BookmarksViewModel([]);
        Bookmarks.Reload(bookmarks, PageLabelFor);
        Bookmarks.BookmarkActivated += pageIndex => LeftPane.GoToPage(pageIndex + 1);
        Bookmarks.OutlineEdited += OnBookmarksEdited;
        Bookmarks.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(BookmarksViewModel.HasBookmarks))
            {
                OnPropertyChanged(nameof(CanGenerateBookmarks));
            }
        };

        Clauses = new ClausesViewModel();
        Clauses.ClauseActivated += pageIndex => LeftPane.GoToPage(pageIndex + 1);
        Clauses.Scanned += OnClausesScanned;
        Clauses.Load(document);

        Annotations = new AnnotationListViewModel();
        Annotations.Reload(document);
        Annotations.AnnotationActivated += OnAnnotationActivated;

        LeftPane.TableRegionSelected += OnTableRegionSelected;
        RightPane.TableRegionSelected += OnTableRegionSelected;

        LeftPane.PropertyChanged += OnLeftPanePropertyChanged;
        RightPane.PropertyChanged += OnRightPanePropertyChanged;
        LeftPane.CustomColorPicked += OnCustomColorPicked;
        RightPane.CustomColorPicked += OnCustomColorPicked;
        Document.Changed += OnDocumentChanged;
        Document.DirtyChanged += OnDocumentDirtyChanged;
        Document.AnnotationsChanged += OnAnnotationsChanged;
        Document.OutlineChanged += OnOutlineChanged;

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

    /// <summary>When on, drag a rectangle over a table to extract it. Mirrored to both panes.</summary>
    [ObservableProperty]
    private bool _tableSelectActive;

    partial void OnTableSelectActiveChanged(bool value)
    {
        LeftPane.TableSelectActive = value;
        RightPane.TableSelectActive = value;
        if (value)
        {
            CommentToolActive = false;
            HighlighterToolActive = false;
            ShapeTool = ShapeTool.None;
        }
    }

    [RelayCommand]
    private void ToggleTableSelect() => TableSelectActive = !TableSelectActive;

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
            TableSelectActive = false;
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

    /// <summary>The highlighter / shape palette for the toolbar swatches (a fresh list on each custom
    /// pick so the "Custom" swatch re-renders in the new colour).</summary>
    [ObservableProperty]
    private System.Collections.Generic.IReadOnlyList<string> _inkColorKeys = [.. AnnotationColors.Keys];

    /// <summary>Raised when the user picks a new "Custom" colour — <see cref="MainViewModel"/> persists it.</summary>
    public event Action<uint>? CustomColorChanged;

    /// <summary>The user-facing name for a 0-based page — its printed label if the document has one, else the ordinal.</summary>
    public string PageLabelFor(int pageIndex) => PageDisplay.Label(Document, pageIndex);

    private void OnCustomColorPicked(uint argb) => ApplyCustomColor(argb);

    /// <summary>Adopt a new "Custom" swatch colour: update the shared value, refresh the rail, select it, persist.</summary>
    public void ApplyCustomColor(uint argb)
    {
        AnnotationColors.CustomColorArgb = argb;
        InkColorKeys = [.. AnnotationColors.Keys];
        InkColorKey = AnnotationColors.Custom;
        CustomColorChanged?.Invoke(argb);
    }

    /// <summary>Whether the Annotations navigation tab is offered (the document has any).</summary>
    public bool HasAnnotations => Document.HasAnnotations;

    /// <summary>Raised when the tab's own close affordance is used.</summary>
    public event EventHandler? CloseRequested;

    // --- Navigation panel ----------------------------------------------------------------

    /// <summary>
    /// From the left icon rail: open the panel to <paramref name="tab"/>, or — if it is already
    /// open on that section — collapse it.
    /// </summary>
    [RelayCommand]
    private void ShowNavigationSection(NavigationTab tab)
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
    private async Task ExportRagChunks()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export RAG chunks",
            Filter = "JSON Lines (*.jsonl)|*.jsonl",
            FileName = (Document.FilePath is { } p ? Path.GetFileNameWithoutExtension(p) : _baseTitle) + ".chunks.jsonl",
            AddExtension = true,
            DefaultExt = ".jsonl",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FileName;
        try
        {
            RagChunkResult result = await Task.Run(() =>
            {
                RagChunkResult r = PdfRagChunker.Chunk(Document);
                PdfRagChunker.WriteJsonl(r.Chunks, path);
                return r;
            });

            long tokens = result.Chunks.Sum(c => (long)c.TokenEstimate);
            string summary =
                $"{result.Chunks.Count} chunks (~{tokens:N0} tokens) from {result.SectionCount} section(s) written to\n{Path.GetFileName(path)}.";
            if (result.PagesWithoutText.Count > 0)
            {
                summary += $"\n\n{result.PagesWithoutText.Count} page(s) had no extractable text and were skipped — OCR is not available yet.";
            }

            MessageBox.Show(summary, "DocDr", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not export chunks: {ex.Message}", "DocDr",
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

    [RelayCommand]
    private void OcrDocument()
    {
        IReadOnlyList<int> blank;
        try
        {
            blank = Document.PagesWithoutText();
        }
        catch (PdfException ex)
        {
            MessageBox.Show($"Could not scan the document: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (blank.Count == 0)
        {
            MessageBox.Show(
                "Every page already has a text layer — nothing to OCR.",
                "DocDr", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Defer past the current click so the modal window activates (see the CLAUDE.md note).
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var viewModel = new OcrProgressViewModel(blank.Count);
            var dialog = new OcrProgressWindow(viewModel) { Owner = Application.Current?.MainWindow };
            var progress = new Progress<OcrProgress>(viewModel.Report);
            OcrResult? result = null;
            Exception? failure = null;

            _ = Task.Run(() =>
                {
                    using var engine = new TesseractOcrEngine();
                    result = Document.AddOcrTextLayer(engine, progress, viewModel.Token);
                })
                .ContinueWith(t =>
                {
                    failure = t.Exception?.GetBaseException();
                    viewModel.Finish();

                    if (failure is not null and not OperationCanceledException)
                    {
                        MessageBox.Show($"OCR failed: {failure.Message}", "DocDr",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                    else if (result is { PagesProcessed: 0 })
                    {
                        MessageBox.Show("No text could be recognised on the scanned pages.", "DocDr",
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());

            dialog.ShowDialog();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    [RelayCommand]
    private async Task TrimMargins()
    {
        int pages = Document.PageCount;
        if (pages > 40 && MessageBox.Show(
                $"Trim the margins on all {pages} pages? Each page is cropped to its visible content. This can be undone.",
                "DocDr", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        var all = Enumerable.Range(0, pages).ToArray();
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            int trimmed = await Task.Run(() => Document.TrimMargins(all)).ConfigureAwait(true);
            if (trimmed == 0)
            {
                MessageBox.Show("Nothing to trim — every page already fits its content.", "DocDr",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (PdfException ex)
        {
            MessageBox.Show($"Could not trim margins: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    /// <summary>True while the document has no outline — the "Generate bookmarks" affordance shows.</summary>
    public bool CanGenerateBookmarks => !Bookmarks.HasBookmarks;

    [RelayCommand]
    private async Task GenerateBookmarks()
    {
        try
        {
            IReadOnlyList<PdfBookmark> outline =
                await Task.Run(() => PdfHeadings.FromHeadings(Document)).ConfigureAwait(true);

            if (outline.Count == 0)
            {
                MessageBox.Show(
                    "No headings could be detected to build bookmarks from.",
                    "DocDr", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Document.SetOutline(outline); // raises Changed → Bookmarks.Reload
        }
        catch (PdfException ex)
        {
            MessageBox.Show($"Could not scan for headings: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
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

    /// <summary>Run <paramref name="action"/> on the UI thread — document events can now be
    /// raised from a background thread (e.g. the OCR run).</summary>
    private static void RunOnUi(Action action)
    {
        System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        _cache.Purge(Document);
        IReadOnlyList<PdfSize> sizes = Document.GetPageSizes();

        LeftPane.ReloadPages(sizes);
        RightPane.ReloadPages(sizes);
        Thumbnails.Reload(sizes);
        Bookmarks.Reload(Document.GetOutline(), PageLabelFor);
        Clauses.Load(Document);
        Annotations.Reload(Document);
        Thumbnails.SetCurrentPage(LeftPane.CurrentPage);

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HasAnnotations));
        RaiseEditState();
    });

    private bool _applyingOutlineEdit;

    // The user renamed / deleted a bookmark: persist the new tree without re-loading the panel
    // (the BookmarksViewModel already holds the edited tree).
    private void OnBookmarksEdited(IReadOnlyList<PdfBookmark> tree)
    {
        _applyingOutlineEdit = true;
        try
        {
            Document.SetOutline(tree);
        }
        finally
        {
            _applyingOutlineEdit = false;
        }
    }

    private void OnOutlineChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        if (!_applyingOutlineEdit)
        {
            Bookmarks.Reload(Document.GetOutline(), PageLabelFor);
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanGenerateBookmarks));
    });

    private void OnAnnotationsChanged(object? sender, AnnotationsChangedEventArgs e) => RunOnUi(() =>
    {
        LeftPane.BuildAnnotationOverlays();
        RightPane.BuildAnnotationOverlays();
        Annotations.Reload(Document);
        OnPropertyChanged(nameof(HasAnnotations));
    });

    private async void OnTableRegionSelected(int pageIndex, PdfRect region)
    {
        TableGrid grid;
        try
        {
            grid = await Task.Run(() => PdfTableExtractor.Extract(Document, pageIndex, region));
        }
        catch (Exception ex) when (ex is PdfException)
        {
            MessageBox.Show($"Could not read that region: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (grid.RowCount == 0)
        {
            MessageBox.Show("No table text was found in that region.", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var window = new TableExtractWindow(new TableExtractViewModel(grid, _baseTitle, pageIndex + 1))
            {
                Owner = Application.Current?.MainWindow,
            };
            window.ShowDialog();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnClausesScanned(PdfCodeStructure structure)
    {
        IReadOnlyDictionary<string, int> map = PdfCrossReferences.BuildPageMap(structure);
        LeftPane.SetClausePageMap(map);
        RightPane.SetClausePageMap(map);
    }

    private void OnAnnotationActivated(int pageIndex, Guid id)
    {
        // Select on the left pane only — the nav list drives that pane, and selecting on both
        // would raise a format toolbar from each.
        LeftPane.SelectedAnnotationId = id;
        RightPane.SelectedAnnotationId = null;
        LeftPane.GoToPage(pageIndex + 1);
    }

    private void OnDocumentDirtyChanged(object? sender, EventArgs e) => RunOnUi(() =>
    {
        OnPropertyChanged(nameof(Title));
        RaiseEditState();
    });

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
        LeftPane.CustomColorPicked -= OnCustomColorPicked;
        RightPane.CustomColorPicked -= OnCustomColorPicked;
        Document.Changed -= OnDocumentChanged;
        Document.DirtyChanged -= OnDocumentDirtyChanged;
        Document.AnnotationsChanged -= OnAnnotationsChanged;
        Document.OutlineChanged -= OnOutlineChanged;
        Bookmarks.OutlineEdited -= OnBookmarksEdited;
        Thumbnails.EditRequested -= OnThumbnailEditRequested;
        Annotations.AnnotationActivated -= OnAnnotationActivated;
        Clauses.Scanned -= OnClausesScanned;
        LeftPane.TableRegionSelected -= OnTableRegionSelected;
        RightPane.TableRegionSelected -= OnTableRegionSelected;
        Clauses.CancelLoad();
        LeftPane.Dispose();
        RightPane.Dispose();
        _cache.Purge(Document);
        Document.Dispose();
    }
}
