using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
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

    public string WindowTitle => SelectedTab is null ? "DocDr" : $"{SelectedTab.Title} — DocDr";

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
            (PdfDocument document,
             System.Collections.Generic.IReadOnlyList<PdfSize> sizes,
             System.Collections.Generic.IReadOnlyList<PdfBookmark> bookmarks) =
                await Task.Run(() =>
                {
                    PdfDocument doc = PdfDocument.Load(path);
                    return (doc, doc.GetPageSizes(), PdfBookmarks.Read(doc));
                }).ConfigureAwait(true);

            var tab = new DocumentTabViewModel(
                UniqueTitle(Path.GetFileName(path)), document, sizes, bookmarks, _renderQueue, _cache);
            tab.CloseRequested += (_, _) => CloseTab(tab);
            Tabs.Add(tab);
            SelectedTab = tab;
            StatusText = $"{document.PageCount} page(s) — {Tabs.Count} tab(s) open.";

            _settings.PushRecentFile(Path.GetFullPath(path));
            _settings.Save();
            RebuildRecentFiles();
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
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
