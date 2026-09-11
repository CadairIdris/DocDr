using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.Pdf;
using Microsoft.Win32;

namespace DocDr.App.ViewModels;

/// <summary>Carries everything needed to jump straight to one match without re-searching the
/// document: every hit the folder scan already found in that file (so Next/Prev and the match
/// count work immediately) plus which one was clicked.</summary>
public sealed record FolderSearchActivation(
    string FilePath,
    IReadOnlyList<FolderSearchHit> KnownHits,
    int TargetPageIndex,
    int TargetCharStart,
    int TargetCharCount,
    string Term,
    bool MatchCase);

/// <summary>One PDF with at least one match, and its matches. A leaf under it is a <see cref="FolderSearchHitViewModel"/>.</summary>
public sealed class FolderSearchDocumentViewModel
{
    public FolderSearchDocumentViewModel(string filePath, IReadOnlyList<FolderSearchHit> hits)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Hits = new ObservableCollection<FolderSearchHitViewModel>(
            hits.Select(h => new FolderSearchHitViewModel(filePath, h, hits)));
    }

    public string FilePath { get; }

    public string FileName { get; }

    public ObservableCollection<FolderSearchHitViewModel> Hits { get; }

    public string Header => $"{FileName} ({Hits.Count})";

    /// <summary>Bound to <c>AutomationProperties.Name</c> — without it, screen readers announce
    /// the tree item's raw CLR type name instead of anything meaningful.</summary>
    public string AutomationName => Header;
}

/// <summary>One match: a page, and a snippet of surrounding text. Pages are shown as a plain
/// 1-based ordinal — the document isn't open, so there's no page-label dictionary to consult.</summary>
public sealed class FolderSearchHitViewModel
{
    public FolderSearchHitViewModel(string filePath, FolderSearchHit hit, IReadOnlyList<FolderSearchHit> siblingHits)
    {
        FilePath = filePath;
        PageIndex = hit.PageIndex;
        CharStart = hit.CharStart;
        CharCount = hit.CharCount;
        Snippet = hit.Snippet;
        SiblingHits = siblingHits;
    }

    public string FilePath { get; }

    public int PageIndex { get; }

    public int CharStart { get; }

    public int CharCount { get; }

    public string Snippet { get; }

    /// <summary>Every hit the folder scan found in this same file, so activating one can hand the
    /// viewer the whole set instead of making it re-search the document.</summary>
    public IReadOnlyList<FolderSearchHit> SiblingHits { get; }

    public string PageLabel => $"p. {PageIndex + 1}";

    /// <summary>Bound to <c>AutomationProperties.Name</c> — without it, screen readers announce
    /// the tree item's raw CLR type name instead of anything meaningful.</summary>
    public string AutomationName => $"{PageLabel}: {Snippet}";
}

/// <summary>
/// Backs the "Find in files" window: pick a folder, search every PDF under it for a term, and
/// show the results as Document &gt; match. Runs on a background thread via
/// <see cref="PdfFolderSearch"/>; results stream in as each file finishes so a large folder feels
/// responsive rather than freezing until the whole scan completes.
/// </summary>
public sealed partial class FolderSearchViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    /// <summary>Filled from the background scan thread(s) with zero UI-thread marshaling per file
    /// — a <see cref="Progress{T}"/> posts to the dispatcher on every single <c>Report</c>, and a
    /// folder with thousands of PDFs turned that into thousands of dispatcher messages, which was
    /// enough to make the window sluggish to even drag around. A timer drains this in batches
    /// instead, so UI work happens a fixed number of times a second regardless of scan speed.</summary>
    private readonly ConcurrentQueue<FolderSearchProgress> _pending = new();
    private DispatcherTimer? _flushTimer;
    private int _filesScanned;
    private int _totalFiles;
    private int _totalMatches;

    private CancellationTokenSource? _cts;
    private string _lastSearchTerm = "";
    private bool _lastMatchCase;

    public FolderSearchViewModel(AppSettings settings)
    {
        _settings = settings;
        _folderPath = settings.LastFindInFilesFolder ?? "";
        _matchCase = settings.LastFindInFilesMatchCase;
        _includeSubfolders = settings.LastFindInFilesSubfolders;
    }

    public ObservableCollection<FolderSearchDocumentViewModel> Results { get; } = [];

    /// <summary>Raised when a match is activated (selected in the tree).</summary>
    public event Action<FolderSearchActivation>? ResultActivated;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _folderPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private string _searchTerm = "";

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private bool _includeSubfolders;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = "Choose a folder and a search term.";

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select a folder to search",
            FolderName = FolderPath,
        };

        if (dialog.ShowDialog() == true)
        {
            FolderPath = dialog.FolderName;
        }
    }

    private bool CanSearch() =>
        !IsSearching && !string.IsNullOrWhiteSpace(FolderPath) && !string.IsNullOrWhiteSpace(SearchTerm);

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        Results.Clear();
        while (_pending.TryDequeue(out _)) { } // drop anything left over from a cancelled run
        _filesScanned = 0;
        _totalFiles = 0;
        _totalMatches = 0;

        string folder = FolderPath.Trim();
        string term = SearchTerm;
        bool matchCase = MatchCase;
        bool subfolders = IncludeSubfolders;
        _lastSearchTerm = term;
        _lastMatchCase = matchCase;

        _settings.LastFindInFilesFolder = folder;
        _settings.LastFindInFilesMatchCase = matchCase;
        _settings.LastFindInFilesSubfolders = subfolders;
        _settings.Save();

        if (!Directory.Exists(folder))
        {
            StatusText = "That folder doesn't exist.";
            return;
        }

        IsSearching = true;
        _flushTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _flushTimer.Tick -= OnFlushTick;
        _flushTimer.Tick += OnFlushTick;
        _flushTimer.Start();
        try
        {
            List<string> files = await Task.Run(() => Directory.EnumerateFiles(
                folder, "*.pdf", subfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList(),
                cts.Token).ConfigureAwait(true);

            if (files.Count == 0)
            {
                StatusText = "No PDF files found in that folder.";
                return;
            }

            StatusText = $"Searching {files.Count} file(s)…";
            var progress = new SyncProgress<FolderSearchProgress>(_pending.Enqueue);
            PdfSearchOptions options = matchCase ? PdfSearchOptions.MatchCase : PdfSearchOptions.None;

            await Task.Run(() => PdfFolderSearch.Search(files, term, options, progress, cts.Token), cts.Token)
                .ConfigureAwait(true);

            _flushTimer.Stop();
            DrainPending(int.MaxValue);

            StatusText = Results.Count == 0
                ? $"No matches in {files.Count} file(s)."
                : $"{_totalMatches} match(es) in {Results.Count} of {files.Count} file(s).";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Search cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not read that folder: {ex.Message}";
        }
        finally
        {
            _flushTimer?.Stop();
            if (ReferenceEquals(_cts, cts))
            {
                IsSearching = false;
                _cts = null;
            }
        }
    }

    private void OnFlushTick(object? sender, EventArgs e) => DrainPending(500);

    /// <summary>Apply up to <paramref name="maxItems"/> queued file results. Capped during a live
    /// scan so one timer tick can't be swamped by a burst of hits; uncapped for the final drain
    /// once the scan itself has finished.</summary>
    private void DrainPending(int maxItems)
    {
        int applied = 0;
        while (applied < maxItems && _pending.TryDequeue(out FolderSearchProgress? p))
        {
            _filesScanned = p.FilesScanned;
            _totalFiles = p.TotalFiles;
            _totalMatches = p.TotalMatches;
            if (p.Hits.Count > 0)
            {
                Results.Add(new FolderSearchDocumentViewModel(p.FilePath, p.Hits));
            }

            applied++;
        }

        if (applied > 0 && IsSearching)
        {
            StatusText = $"Scanned {_filesScanned} / {_totalFiles} file(s) — {_totalMatches} match(es)…";
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    public void Activate(FolderSearchHitViewModel? hit)
    {
        if (hit is not null)
        {
            ResultActivated?.Invoke(new FolderSearchActivation(
                hit.FilePath, hit.SiblingHits, hit.PageIndex, hit.CharStart, hit.CharCount,
                _lastSearchTerm, _lastMatchCase));
        }
    }

    /// <summary>An <see cref="IProgress{T}"/> that just enqueues — unlike <see cref="Progress{T}"/>,
    /// it does not post to the UI thread's dispatcher on every call. See <see cref="_pending"/>.</summary>
    private sealed class SyncProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
