using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>One node of the detected clause tree (a numbered section of a design code).</summary>
public sealed partial class ClauseNodeViewModel : ObservableObject
{
    public ClauseNodeViewModel(PdfClause clause, int depth)
    {
        Number = clause.Number;
        Title = clause.Title;
        Display = clause.Display;
        PageIndex = clause.PageIndex;
        Children = new ObservableCollection<ClauseNodeViewModel>(
            clause.Children.Select(c => new ClauseNodeViewModel(c, depth + 1)));
        _isExpanded = depth < 1; // chapters open, subclauses collapsed
    }

    public string Number { get; }

    public string Title { get; }

    public string Display { get; }

    public int PageIndex { get; }

    public string PageLabel => (PageIndex + 1).ToString();

    public ObservableCollection<ClauseNodeViewModel> Children { get; }

    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>
/// The clause index for a tab — built in the background from <see cref="PdfClauses.Read"/> since the
/// scan reads every page. Clicking a node jumps the left pane to that page.
/// </summary>
public sealed partial class ClausesViewModel : ObservableObject
{
    private CancellationTokenSource? _load;
    private string _sourceName = "this document";

    public ObservableCollection<ClauseNodeViewModel> Roots { get; } = [];

    public bool HasClauses => Roots.Count > 0;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>True once a scan has finished and found nothing (so we can show the "none" hint).</summary>
    public bool IsEmptyResult => !IsLoading && Roots.Count == 0 && _hasScanned;

    private bool _hasScanned;

    /// <summary>Raised when a clause is chosen; carries the 0-based page index.</summary>
    public event Action<int>? ClauseActivated;

    /// <summary>Raised on the UI thread when a scan finishes, with the detected clause tree.</summary>
    public event Action<IReadOnlyList<PdfClause>>? Scanned;

    /// <summary>(Re)scan the document for clause headings on a background thread.</summary>
    public void Load(PdfDocument document)
    {
        _sourceName = document.FilePath is { Length: > 0 } path
            ? System.IO.Path.GetFileNameWithoutExtension(path)
            : "this document";

        _load?.Cancel();
        _load?.Dispose();
        _load = new CancellationTokenSource();
        CancellationToken token = _load.Token;

        Roots.Clear();
        _hasScanned = false;
        IsLoading = true;
        RaiseState();

        Task.Run(() => PdfClauses.Read(document, token), token).ContinueWith(
            task =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Roots.Clear();
                IReadOnlyList<PdfClause> result = task.Status == TaskStatus.RanToCompletion ? task.Result : [];
                foreach (PdfClause clause in result)
                {
                    Roots.Add(new ClauseNodeViewModel(clause, 0));
                }

                _hasScanned = true;
                IsLoading = false;
                RaiseState();
                Scanned?.Invoke(result);
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Activate(ClauseNodeViewModel? node)
    {
        if (node is not null)
        {
            ClauseActivated?.Invoke(node.PageIndex);
        }
    }

    [RelayCommand]
    private void CopyCitation(ClauseNodeViewModel? node)
    {
        if (node is not null)
        {
            Citations.CopyToClipboard(
                Citations.ForClause(_sourceName, node.Number, node.Title, node.PageIndex + 1));
        }
    }

    public void CancelLoad()
    {
        _load?.Cancel();
        _load?.Dispose();
        _load = null;
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasClauses));
        OnPropertyChanged(nameof(IsEmptyResult));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmptyResult));
}
