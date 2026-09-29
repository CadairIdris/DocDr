using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.App.Views;
using DocDr.Pdf;
using Microsoft.Win32;

namespace DocDr.App.ViewModels;

/// <summary>
/// The window shell: a tab strip of open documents. Opening a file adds a tab; the same file
/// may be opened more than once. Rendering infrastructure (queue, page cache) is shared by
/// every tab and is safe to be — all PDFium work is serialised inside <see cref="PdfDocument"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly BackgroundRenderQueue _renderQueue;
    private readonly PageImageService _images;
    private readonly AppSettings _settings;

    public MainViewModel(BackgroundRenderQueue renderQueue, PageImageService images, AppSettings settings)
    {
        _renderQueue = renderQueue;
        _images = images;
        _settings = settings;
        AnnotationColors.CustomColorArgb = settings.LastCustomColor;
        Tabs.CollectionChanged += OnTabsChanged;
        LeftGroup.IsActive = true;
        _activeGroup = LeftGroup;
        LeftGroup.PropertyChanged += OnGroupPropertyChanged;
        RightGroup.PropertyChanged += OnGroupPropertyChanged;
        RebuildRecentFiles();
    }

    /// <summary>Every open tab, whichever side it's docked on (save-on-exit, title de-duplication,
    /// "is this file already open"). <see cref="LeftGroup"/> / <see cref="RightGroup"/> hold the
    /// same tabs split by side.</summary>
    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = [];

    // --- Side-by-side tab groups ------------------------------------------------------------

    /// <summary>The main (left) tab strip. Never empty while any tab is open.</summary>
    public TabGroupViewModel LeftGroup { get; } = new(isPrimary: true);

    /// <summary>The right tab strip — only has tabs while documents are docked side by side.</summary>
    public TabGroupViewModel RightGroup { get; } = new(isPrimary: false);

    private TabGroupViewModel _activeGroup;

    /// <summary>The side the toolbar acts on — the one last clicked or focused into.</summary>
    public TabGroupViewModel ActiveGroup => _activeGroup;

    /// <summary>Two documents are docked side by side.</summary>
    public bool IsSideBySide => RightGroup.HasTabs;

    /// <summary>Read mode shows only the tab being read; otherwise both sides show when docked.</summary>
    public bool ShowLeftGroup => !IsReadMode || _activeGroup == LeftGroup;

    public bool ShowRightGroup => IsSideBySide && (!IsReadMode || _activeGroup == RightGroup);

    /// <summary>The tab the toolbar, title bar and shortcuts act on: the active side's selected tab.</summary>
    public DocumentTabViewModel? SelectedTab
    {
        get => _activeGroup.SelectedTab;
        set
        {
            if (value is null)
            {
                _activeGroup.SelectedTab = null;
                return;
            }

            TabGroupViewModel group = GroupOf(value) ?? _activeGroup;
            ActivateGroup(group);
            group.SelectedTab = value;
        }
    }

    public TabGroupViewModel? GroupOf(DocumentTabViewModel tab) =>
        LeftGroup.Tabs.Contains(tab) ? LeftGroup
        : RightGroup.Tabs.Contains(tab) ? RightGroup
        : null;

    /// <summary>Make <paramref name="group"/> the side the toolbar acts on (the view calls this when
    /// the user clicks or tabs into it).</summary>
    public void ActivateGroup(TabGroupViewModel group)
    {
        if (group == _activeGroup || (!group.IsPrimary && !group.HasTabs))
        {
            return;
        }

        _activeGroup.IsActive = false;
        _activeGroup = group;
        group.IsActive = true;
        OnPropertyChanged(nameof(ActiveGroup));
        OnPropertyChanged(nameof(ShowLeftGroup));
        OnPropertyChanged(nameof(ShowRightGroup));
        RaiseSelectedTabChanged();
    }

    /// <summary>Whether dragging / moving <paramref name="tab"/> to <paramref name="target"/> would
    /// do anything useful. Splitting the only open tab off to the right would leave the left side
    /// empty (and it would just slide back), so that's refused; moving the last right-hand tab
    /// back to the left is allowed — it ends side-by-side.</summary>
    public bool CanMoveTab(DocumentTabViewModel tab, TabGroupViewModel target)
    {
        TabGroupViewModel? source = GroupOf(tab);
        return source is not null && source != target
            && !(source.IsPrimary && source.Tabs.Count == 1 && !target.HasTabs);
    }

    /// <summary>Dock <paramref name="tab"/> on the other side (context menu / toolbar).</summary>
    public void MoveTabToOtherSide(DocumentTabViewModel tab)
    {
        if (GroupOf(tab) is { } source)
        {
            MoveTab(tab, source.IsPrimary ? RightGroup : LeftGroup);
        }
    }

    /// <summary>Move <paramref name="tab"/> into <paramref name="target"/>, select it there and make
    /// that side active. The side it left selects its neighbour; if the left side is emptied the
    /// right side's tabs slide over, so the left strip is never empty while tabs are open.</summary>
    public void MoveTab(DocumentTabViewModel tab, TabGroupViewModel target)
    {
        if (!CanMoveTab(tab, target) || GroupOf(tab) is not { } source)
        {
            return;
        }

        Rearranging(() =>
        {
            RemoveFromGroup(source, tab);
            target.Tabs.Add(tab);
            target.SelectedTab = tab;
            ActivateGroup(target);
            NormaliseGroups();
        });
        StatusText = IsSideBySide
            ? "Side by side — drag a tab across, or use Link scrolling to keep the two in step."
            : $"{Tabs.Count} tab(s) open.";
    }

    private static void RemoveFromGroup(TabGroupViewModel group, DocumentTabViewModel tab)
    {
        int index = group.Tabs.IndexOf(tab);
        bool wasSelected = ReferenceEquals(group.SelectedTab, tab);
        group.Tabs.Remove(tab);
        if (wasSelected || group.SelectedTab is null)
        {
            group.SelectedTab = group.Tabs.Count > 0 ? group.Tabs[Math.Clamp(index, 0, group.Tabs.Count - 1)] : null;
        }
    }

    /// <summary>Keep the left strip populated and the active side valid after tabs move or close.</summary>
    private void NormaliseGroups()
    {
        if (!LeftGroup.HasTabs && RightGroup.HasTabs)
        {
            DocumentTabViewModel? selected = RightGroup.SelectedTab;
            foreach (DocumentTabViewModel moved in RightGroup.Tabs.ToList())
            {
                RightGroup.Tabs.Remove(moved);
                LeftGroup.Tabs.Add(moved);
            }

            RightGroup.SelectedTab = null;
            LeftGroup.SelectedTab = selected ?? LeftGroup.Tabs.FirstOrDefault();
        }

        if (!RightGroup.HasTabs)
        {
            ActivateGroup(LeftGroup);
        }

        OnPropertyChanged(nameof(IsSideBySide));
        OnPropertyChanged(nameof(ShowLeftGroup));
        OnPropertyChanged(nameof(ShowRightGroup));
        RelinkScrolling();
    }

    private void OnGroupPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabGroupViewModel.SelectedTab))
        {
            if (sender == _activeGroup)
            {
                RaiseSelectedTabChanged();
            }

            RelinkScrolling();
        }
    }

    // --- Linked scrolling -------------------------------------------------------------------

    /// <summary>Keep the two side-by-side documents scrolled and zoomed together (toolbar toggle).
    /// Stays on as a preference while nothing is docked; it takes effect when two documents are.</summary>
    [ObservableProperty]
    private bool _isScrollLinked;

    private ScrollLink? _scrollLink;
    private int _relinkSuspended;

    /// <summary>Run a multi-step tab rearrangement with relinking held off, then link once.</summary>
    private void Rearranging(Action action)
    {
        _relinkSuspended++;
        try
        {
            action();
        }
        finally
        {
            _relinkSuspended--;
        }

        RelinkScrolling();
    }

    /// <summary>The active link, for tests / diagnostics (null unless linked and side by side).</summary>
    public ScrollLink? ActiveScrollLink => _scrollLink;

    partial void OnIsScrollLinkedChanged(bool value) => RelinkScrolling();

    /// <summary>(Re)build the link between the two visible documents. Called whenever either side's
    /// visible tab changes, so the page pairing is re-taken from wherever the two now are.</summary>
    private void RelinkScrolling()
    {
        if (_relinkSuspended > 0)
        {
            return; // mid-move: selection passes through intermediate states — link once at the end
        }

        PdfPaneViewModel? left = LeftGroup.SelectedTab?.LeftPane;
        PdfPaneViewModel? right = RightGroup.SelectedTab?.LeftPane;
        bool want = IsScrollLinked && left is not null && right is not null;

        if (_scrollLink is not null && want
            && ReferenceEquals(_scrollLink.A, left) && ReferenceEquals(_scrollLink.B, right))
        {
            return; // already linking exactly these two
        }

        _scrollLink?.Dispose();
        _scrollLink = want ? new ScrollLink(left!, right!) : null;
        OnPropertyChanged(nameof(ActiveScrollLink));
        if (_scrollLink is not null && _scrollLink.PageOffset != 0)
        {
            StatusText = $"Scrolling linked — page offset {_scrollLink.PageOffset:+#;-#;0} (line the two up, then re-link to change it).";
        }
    }

    private DocumentTabViewModel? _lastSelectedTab;

    /// <summary>Re-publish <see cref="SelectedTab"/> after the active side or its selection moved,
    /// doing what the old auto-property's change hooks did: follow the new tab's title, and leave
    /// read mode if a different tab took over.</summary>
    private void RaiseSelectedTabChanged()
    {
        DocumentTabViewModel? value = SelectedTab;
        if (ReferenceEquals(value, _lastSelectedTab))
        {
            return;
        }

        OnSelectedTabChanging(_lastSelectedTab, value);
        _lastSelectedTab = value;
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(WindowTitle));
        OnSelectedTabChanged(value);
    }

    /// <summary>Recent documents shown on the home screen (most recent first).</summary>
    public ObservableCollection<RecentFileViewModel> RecentFiles { get; } = [];

    public bool HasRecentFiles => RecentFiles.Count > 0;

    private void OnSelectedTabChanging(DocumentTabViewModel? oldValue, DocumentTabViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnSelectedTabPropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnSelectedTabPropertyChanged;
        }
    }

    private void OnSelectedTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentTabViewModel.Title))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    [ObservableProperty]
    private string _statusText = "Open a PDF to begin.";

    public System.Array Themes { get; } = Enum.GetValues<AppTheme>();

    [ObservableProperty]
    private AppTheme _selectedTheme = (Application.Current as App)?.Theme.Current ?? AppTheme.System;

    partial void OnSelectedThemeChanged(AppTheme value) =>
        (Application.Current as App)?.Theme.Apply(value);

    public bool HasTabs => Tabs.Count > 0;

    public string WindowTitle => SelectedTab is null
        ? $"DocDr {DiagnosticsLog.Version}"
        : $"{SelectedTab.Title} — DocDr {DiagnosticsLog.Version}";

    /// <summary>Shown on the home screen and in the About box; carries the git SHA.</summary>
    public string AppVersionLabel => $"version {DiagnosticsLog.InformationalVersion}";

    [RelayCommand]
    private static void About() => MessageBox.Show(
        $"DocDr {DiagnosticsLog.InformationalVersion}\n\n" +
        $".NET {System.Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n\n" +
        $"Logs: {DiagnosticsLog.LogDirectory}\n\n" +
        "Bundled open-source components (PDFium, Tesseract OCR, …) — see THIRD-PARTY-NOTICES.md.",
        "About DocDr", MessageBoxButton.OK, MessageBoxImage.Information);

    // --- Read mode -------------------------------------------------------------------------

    /// <summary>Full-screen, chrome-free, two-page reading view. The window watches this.</summary>
    [ObservableProperty]
    private bool _isReadMode;

    private ViewMode _modeBeforeReading = ViewMode.Continuous;
    private bool _splitBeforeReading;
    private DocumentTabViewModel? _readingTab;

    partial void OnIsReadModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLeftGroup));
        OnPropertyChanged(nameof(ShowRightGroup));

        if (value && SelectedTab is null)
        {
            return;
        }

        if (value)
        {
            _readingTab = SelectedTab;
            _modeBeforeReading = _readingTab!.LeftPane.Mode;
            _splitBeforeReading = _readingTab.IsSplitView;
            _readingTab.IsSplitView = false;
            _readingTab.IsNavigationPanelVisible = false;
            _readingTab.LeftPane.Mode = ViewMode.TwoPage;
        }
        else if (_readingTab is not null)
        {
            _readingTab.LeftPane.Mode = _modeBeforeReading;
            _readingTab.IsSplitView = _splitBeforeReading;
            _readingTab = null;
        }
    }

    private void OnSelectedTabChanged(DocumentTabViewModel? value)
    {
        // Switching documents leaves read mode — the reading view is tied to one tab.
        if (IsReadMode && !ReferenceEquals(value, _readingTab))
        {
            IsReadMode = false;
        }
    }

    [RelayCommand]
    private void ToggleReadMode()
    {
        if (HasTabs || IsReadMode)
        {
            IsReadMode = !IsReadMode;
        }
    }

    [RelayCommand]
    private void ExitReadMode() => IsReadMode = false;

    /// <summary>Turn one spread in read mode (<paramref name="direction"/> +1 forward, -1 back).</summary>
    public void TurnReadingPage(int direction) => SelectedTab?.LeftPane.Advance(direction);

    [RelayCommand]
    private async Task OpenAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open PDF",
            Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (string file in dialog.FileNames)
        {
            await OpenPathAsync(file).ConfigureAwait(true);
        }
    }

    public async Task OpenPathAsync(string path)
    {
        StatusText = $"Opening {Path.GetFileName(path)}…";
        try
        {
            PdfDocument document = await Task.Run(() =>
            {
                PdfDocument doc = PdfDocument.Load(path);
                _ = doc.GetPageSizes();
                return doc;
            }).ConfigureAwait(true);

            AddDocumentTab(document, UniqueTitle(Path.GetFileName(path)));

            _settings.PushRecentFile(Path.GetFullPath(path));
            _settings.Save();
            RebuildRecentFiles();
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    /// <summary>Add a tab for an already-loaded document (merge output, a new blank doc, an opened file).</summary>
    public void AddDocumentTab(PdfDocument document, string title)
    {
        document.ImageDecoder = new AppImageDecoder();

        var tab = new DocumentTabViewModel(
            UniqueTitle(title), document, document.GetPageSizes(), document.GetOutline(),
            _renderQueue, _images);
        tab.CloseRequested += (_, _) => CloseTab(tab);
        tab.MoveToOtherSideRequested += (_, _) => MoveTabToOtherSide(tab);
        tab.CustomColorChanged += argb =>
        {
            _settings.LastCustomColor = argb;
            _settings.Save();
        };
        Rearranging(() =>
        {
            Tabs.Add(tab);
            _activeGroup.Tabs.Add(tab); // opens on whichever side is active
            SelectedTab = tab;
            NormaliseGroups();
        });
        StatusText = $"{document.PageCount} page(s) — {Tabs.Count} tab(s) open.";
    }

    [RelayCommand]
    private void NewDocument()
    {
        var viewModel = new NewDocumentViewModel(_settings);
        var window = new NewDocumentWindow(viewModel) { Owner = Application.Current?.MainWindow };
        PdfSize? size = null;
        viewModel.Confirmed = s => { size = s; window.Close(); };
        window.ShowDialog();
        if (size is not { } chosen)
        {
            return;
        }

        try
        {
            AddDocumentTab(PdfDocument.CreateBlank(chosen), "Untitled");
        }
        catch (PdfException ex)
        {
            MessageBox.Show($"Could not create the document: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select PDFs to merge",
            Filter = "PDF documents (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var viewModel = new MergeViewModel(dialog.FileNames);
        var window = new MergeWindow(viewModel) { Owner = Application.Current?.MainWindow };
        MergeRequest? request = null;
        viewModel.Confirmed = r => { request = r; window.Close(); };
        window.ShowDialog();
        if (request is null || request.Files.Count == 0)
        {
            return;
        }

        StatusText = $"Merging {request.Files.Count} files…";
        try
        {
            PdfDocument merged = await Task.Run(() =>
            {
                MergeResult result = PdfDocument.Merge(request.Files.Select(f => f.Path).ToArray());
                if (request.AddBookmarks)
                {
                    var roots = new System.Collections.Generic.List<PdfBookmark>(request.Files.Count);
                    for (int i = 0; i < request.Files.Count; i++)
                    {
                        int start = result.SourceStartPages[i];
                        IReadOnlyList<PdfBookmark> subs = [];
                        try
                        {
                            using PdfDocument src = PdfDocument.Load(request.Files[i].Path);
                            subs = PdfHeadings.Shift(PdfHeadings.SubHeadings(src), start);
                        }
                        catch (PdfException)
                        {
                            // no sub-headings for this file
                        }

                        roots.Add(new PdfBookmark(request.Files[i].Title, start, subs));
                    }

                    result.Document.SetOutline(roots);
                }

                _ = result.Document.GetPageSizes();
                return result.Document;
            }).ConfigureAwait(true);

            AddDocumentTab(merged, "Merged document");
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Merge failed: {ex.Message}";
            MessageBox.Show($"Could not merge those files: {ex.Message}", "DocDr",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Find in files -----------------------------------------------------------------

    private FolderSearchWindow? _findInFilesWindow;

    [RelayCommand]
    private void FindInFiles()
    {
        if (_findInFilesWindow is not null)
        {
            _findInFilesWindow.Activate();
            return;
        }

        var viewModel = new FolderSearchViewModel(_settings);
        var window = new FolderSearchWindow(viewModel) { Owner = Application.Current?.MainWindow };
        viewModel.ResultActivated += OnFindInFilesResultActivated;
        window.Closed += (_, _) =>
        {
            viewModel.ResultActivated -= OnFindInFilesResultActivated;
            _findInFilesWindow = null;
        };

        _findInFilesWindow = window;
        window.Show();
    }

    private async void OnFindInFilesResultActivated(FolderSearchActivation activation) =>
        await OpenSearchResultAsync(activation).ConfigureAwait(true);

    /// <summary>Open (or switch to) the tab for the activated file and jump the left pane straight
    /// to that match, using the hits the folder scan already found rather than re-searching the
    /// whole document (which was the slow part — a big book has to be scanned page by page either
    /// way, so doing it twice roughly doubled the wait).</summary>
    public async Task OpenSearchResultAsync(FolderSearchActivation activation)
    {
        DocumentTabViewModel? tab = Tabs.FirstOrDefault(t => t.FilePath is { } p && PathsEqual(p, activation.FilePath));
        if (tab is null)
        {
            await OpenPathAsync(activation.FilePath).ConfigureAwait(true);
            tab = SelectedTab;
        }
        else
        {
            SelectedTab = tab;
        }

        if (tab is null)
        {
            return; // open failed; OpenPathAsync already updated StatusText
        }

        Application.Current?.MainWindow?.Activate();
        await tab.LeftPane.GoToSearchHitAsync(
            activation.KnownHits, activation.TargetPageIndex, activation.TargetCharStart,
            activation.TargetCharCount, activation.Term, activation.MatchCase).ConfigureAwait(true);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // --- Recent files ------------------------------------------------------------------

    [RelayCommand]
    private async Task OpenRecentAsync(RecentFileViewModel? recent)
    {
        if (recent is null)
        {
            return;
        }

        if (!File.Exists(recent.Path))
        {
            StatusText = $"{recent.FileName} is no longer at that location.";
            _settings.RecentFiles.RemoveAll(p => string.Equals(p, recent.Path, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            RebuildRecentFiles();
            return;
        }

        await OpenPathAsync(recent.Path).ConfigureAwait(true);
    }

    [RelayCommand]
    private void ClearRecentFiles()
    {
        _settings.RecentFiles.Clear();
        _settings.Save();
        RebuildRecentFiles();
    }

    [RelayCommand]
    private void RemoveRecentFile(RecentFileViewModel? recent)
    {
        if (recent is null)
        {
            return;
        }

        _settings.RecentFiles.RemoveAll(p => string.Equals(p, recent.Path, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        RebuildRecentFiles();
    }

    private void RebuildRecentFiles()
    {
        RecentFiles.Clear();
        foreach (string path in _settings.RecentFiles)
        {
            RecentFiles.Add(new RecentFileViewModel(path));
        }

        OnPropertyChanged(nameof(HasRecentFiles));
    }

    [RelayCommand]
    private void CloseTab(DocumentTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        if (!Tabs.Contains(tab) || !tab.ConfirmClose())
        {
            return;
        }

        Rearranging(() =>
        {
            if (GroupOf(tab) is { } group)
            {
                RemoveFromGroup(group, tab);
            }

            Tabs.Remove(tab);
            NormaliseGroups(); // may slide the right side over, end side-by-side, and drop the link
        });
        _renderQueue.Clear();

        tab.Dispose();
        StatusText = Tabs.Count > 0 ? $"{Tabs.Count} tab(s) open." : "Open a PDF to begin.";
    }

    private string UniqueTitle(string fileName)
    {
        var taken = Tabs.Select(t => t.Title).ToHashSet();
        if (!taken.Contains(fileName))
        {
            return fileName;
        }

        for (int n = 2; ; n++)
        {
            string candidate = $"{fileName} ({n})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Prompt to save every dirty tab. Returns false if the user cancelled (abort the shutdown).</summary>
    public bool ConfirmShutdown()
    {
        foreach (DocumentTabViewModel tab in Tabs.ToArray())
        {
            if (!tab.ConfirmClose())
            {
                return false;
            }
        }

        return true;
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(WindowTitle));
    }

    public void Dispose()
    {
        _scrollLink?.Dispose();
        _scrollLink = null;
        foreach (DocumentTabViewModel tab in Tabs)
        {
            tab.Dispose();
        }

        Tabs.Clear();
        _renderQueue.Dispose();
    }
}
