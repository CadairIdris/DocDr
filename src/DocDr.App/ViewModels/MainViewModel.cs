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
    private readonly CachingPageRenderer _cache;
    private readonly AppSettings _settings;

    public MainViewModel(BackgroundRenderQueue renderQueue, CachingPageRenderer cache, AppSettings settings)
    {
        _renderQueue = renderQueue;
        _cache = cache;
        _settings = settings;
        AnnotationColors.CustomColorArgb = settings.LastCustomColor;
        Tabs.CollectionChanged += OnTabsChanged;
        RebuildRecentFiles();
    }

    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = [];

    /// <summary>Recent documents shown on the home screen (most recent first).</summary>
    public ObservableCollection<RecentFileViewModel> RecentFiles { get; } = [];

    public bool HasRecentFiles => RecentFiles.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private DocumentTabViewModel? _selectedTab;

    partial void OnSelectedTabChanging(DocumentTabViewModel? oldValue, DocumentTabViewModel? newValue)
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

    partial void OnSelectedTabChanged(DocumentTabViewModel? value)
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
            _renderQueue, _cache);
        tab.CloseRequested += (_, _) => CloseTab(tab);
        tab.CustomColorChanged += argb =>
        {
            _settings.LastCustomColor = argb;
            _settings.Save();
        };
        Tabs.Add(tab);
        SelectedTab = tab;
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

        int index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        if (!tab.ConfirmClose())
        {
            return;
        }

        Tabs.Remove(tab);
        _renderQueue.Clear();

        if (ReferenceEquals(SelectedTab, tab) || SelectedTab is null)
        {
            SelectedTab = Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : null;
        }

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
        foreach (DocumentTabViewModel tab in Tabs)
        {
            tab.Dispose();
        }

        Tabs.Clear();
        _renderQueue.Dispose();
    }
}
