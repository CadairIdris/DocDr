using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.Pdf;
using Microsoft.Win32;

namespace DocDr.App.ViewModels;

/// <summary>Word boxes to outline on the comparison image: the old words (red) and the new (blue).</summary>
public sealed record TextHighlight(IReadOnlyList<PageBox> Old, IReadOnlyList<PageBox> New);

/// <summary>An outline on the comparison image, in DIPs at 100 %.</summary>
public sealed record CompareHighlightViewModel(double X, double Y, double Width, double Height, bool IsOld);

/// <summary>One row of the text-changes list.</summary>
public sealed class TextChangeItemViewModel(TextChange change)
{
    public TextChange Change { get; } = change;

    public string Header
    {
        get
        {
            string where = Change.Clause is { } c ? $" · {ClauseComparison.DisplayOf(c)}" : string.Empty;
            return $"{Change.Index + 1}. {KindLabel}{where} · {PagesLabel(Change.OldPage, Change.NewPage)}";
        }
    }

    public string KindLabel => Change.Kind switch
    {
        TextChangeKind.Inserted => "Inserted",
        TextChangeKind.Deleted => "Deleted",
        TextChangeKind.Moved when Change.MovedPartner is int p => $"Moved (see {p + 1})",
        TextChangeKind.Moved => "Moved",
        _ => "Changed",
    };

    public IReadOnlyList<RedlineSegment> Segments => Change.Segments;

    public string AutomationName => $"{Header}: {string.Join(" ", Segments.Select(s => s.Text))}";

    internal static string PagesLabel(int? oldPage, int? newPage) => (oldPage, newPage) switch
    {
        (int o, int n) when o == n => $"p. {o + 1}",
        (int o, int n) => $"p. {o + 1} → {n + 1}",
        (int o, null) => $"old p. {o + 1}",
        (null, int n) => $"new p. {n + 1}",
        _ => string.Empty,
    };
}

/// <summary>One row of the clause comparison.</summary>
public sealed class ClauseRowViewModel(ClauseComparison clause)
{
    public ClauseComparison Clause { get; } = clause;

    public string NumberLabel => PdfClauseCompare.NumberLabel(Clause);

    public string Title => Clause.Title;

    public string KindLabel => PdfClauseCompare.KindLabel(Clause);

    /// <summary>Short status for the list: "modified 82 %", "renumbered", "added"…</summary>
    public string Status => Clause.Kind switch
    {
        ClauseChangeKind.Unchanged => "same",
        ClauseChangeKind.Modified => string.Create(CultureInfo.InvariantCulture, $"{Clause.Similarity * 100:0} %"),
        ClauseChangeKind.Renumbered => "renumbered",
        ClauseChangeKind.Added => "added",
        _ => "removed",
    };

    public ClauseChangeKind Kind => Clause.Kind;

    public Thickness Indent => new(Math.Max(0, Clause.Level - 1) * 12, 0, 0, 0);

    public string? TitleChange => Clause.TitleChanged ? $"Title was “{Clause.OldTitle}”" : null;

    public string PagesLabel => TextChangeItemViewModel.PagesLabel(Clause.OldPage, Clause.NewPage);

    public IReadOnlyList<RedlineSegment> Redline => Clause.Redline;

    public string AutomationName => $"{NumberLabel} {Title}, {KindLabel}";
}

/// <summary>The window's text (stage 2) and clause (stage 3) comparisons, run when their tab is first opened.</summary>
public sealed partial class VisualCompareViewModel
{
    private CancellationTokenSource? _textCts;
    private CancellationTokenSource? _clauseCts;
    private TextCompareResult? _textResult;
    private ClauseCompareResult? _clauseResult;
    private bool _textRequested;
    private bool _clauseRequested;

    public ObservableCollection<CompareHighlightViewModel> Highlights { get; } = [];

    public ObservableCollection<TextChangeItemViewModel> TextChanges { get; } = [];

    public ObservableCollection<ClauseRowViewModel> Clauses { get; } = [];

    /// <summary>0 = pages, 1 = text, 2 = clauses.</summary>
    [ObservableProperty]
    private int _selectedPanelIndex;

    [ObservableProperty]
    private TextChangeItemViewModel? _selectedTextChange;

    [ObservableProperty]
    private bool _isTextComparing;

    [ObservableProperty]
    private string _textSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportTextReportCommand))]
    private bool _hasTextResult;

    [ObservableProperty]
    private bool _ignoreCase;

    [ObservableProperty]
    private ClauseRowViewModel? _selectedClause;

    [ObservableProperty]
    private bool _isClauseComparing;

    [ObservableProperty]
    private string _clauseSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportClauseReportCommand))]
    private bool _hasClauseResult;

    [ObservableProperty]
    private bool _showUnchangedClauses;

    partial void OnSelectedPanelIndexChanged(int value)
    {
        if (value == 1 && !_textRequested)
        {
            RunTextCompare();
        }
        else if (value == 2 && !_clauseRequested)
        {
            RunClauseCompare();
        }
    }

    /// <summary>Swap made both results wrong: drop them, and re-run the one on screen.</summary>
    private void InvalidateTextResults()
    {
        _textCts?.Cancel();
        _clauseCts?.Cancel();
        _textRequested = _clauseRequested = false;
        _textResult = null;
        _clauseResult = null;
        TextChanges.Clear();
        Clauses.Clear();
        HasTextResult = HasClauseResult = false;
        IsTextComparing = IsClauseComparing = false;
        TextSummary = ClauseSummary = string.Empty;
        OnSelectedPanelIndexChanged(SelectedPanelIndex);
    }

    // --- Text ---------------------------------------------------------------------------------

    partial void OnIgnoreCaseChanged(bool value)
    {
        if (_textRequested)
        {
            RunTextCompare();
        }
    }

    private void RunTextCompare()
    {
        _textCts?.Cancel();
        var cts = new CancellationTokenSource();
        _textCts = cts;
        _textRequested = true;
        PdfDocument oldDoc = _oldDoc, newDoc = _newDoc;
        var options = new TextCompareOptions { IgnoreCase = IgnoreCase };
        IsTextComparing = true;
        HasTextResult = false;
        TextChanges.Clear();
        TextSummary = "Reading and comparing the text…";

        Task.Run(() => PdfTextCompare.Compare(oldDoc, newDoc, options, cts.Token), cts.Token).ContinueWith(t =>
        {
            _ = t.Exception;
            if (cts.IsCancellationRequested)
            {
                return;
            }

            IsTextComparing = false;
            if (t.IsFaulted)
            {
                TextSummary = "Couldn't compare the text: " + t.Exception?.GetBaseException().Message;
                return;
            }

            TextCompareResult r = t.Result;
            _textResult = r;
            HasTextResult = true;
            foreach (TextChange c in r.Changes)
            {
                TextChanges.Add(new TextChangeItemViewModel(c));
            }

            TextSummary = r.OldWords + r.NewWords == 0
                ? "Neither document has a text layer to compare (scanned? run OCR first)."
                : string.Create(CultureInfo.CurrentCulture,
                    $"{(r.Changes.Count == 1 ? "1 change" : $"{r.Changes.Count:N0} changes")} · {r.WordsDeleted:N0} words deleted · {r.WordsInserted:N0} inserted · {r.Similarity:P1} unchanged");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    partial void OnSelectedTextChangeChanged(TextChangeItemViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        TextChange c = value.Change;
        SelectPageRowQuietly(c.OldPage, c.NewPage);
        ShowPair(c.OldPage, c.NewPage, new TextHighlight(c.OldBoxes, c.NewBoxes));
    }

    [RelayCommand(CanExecute = nameof(HasTextResult))]
    private void ExportTextReport()
    {
        if (_textResult is { } r && AskReportPath("text changes") is { } path)
        {
            SaveReport(() => PdfTextCompare.WriteReport(r, _oldName, _newName, path), path);
        }
    }

    // --- Clauses ------------------------------------------------------------------------------

    partial void OnShowUnchangedClausesChanged(bool value) => FillClauses();

    private void RunClauseCompare()
    {
        _clauseCts?.Cancel();
        var cts = new CancellationTokenSource();
        _clauseCts = cts;
        _clauseRequested = true;
        PdfDocument oldDoc = _oldDoc, newDoc = _newDoc;
        IsClauseComparing = true;
        HasClauseResult = false;
        Clauses.Clear();
        ClauseSummary = "Finding the clauses of both editions and matching them…";

        Task.Run(() => PdfClauseCompare.Compare(oldDoc, newDoc, cts.Token), cts.Token).ContinueWith(t =>
        {
            _ = t.Exception;
            if (cts.IsCancellationRequested)
            {
                return;
            }

            IsClauseComparing = false;
            if (t.IsFaulted)
            {
                ClauseSummary = "Couldn't compare the clauses: " + t.Exception?.GetBaseException().Message;
                return;
            }

            ClauseCompareResult r = t.Result;
            _clauseResult = r;
            HasClauseResult = true;
            FillClauses();
            ClauseSummary = r.OldClauseCount <= 1 && r.NewClauseCount <= 1
                ? "No numbered clauses found — use the Text tab for this document."
                : string.Create(CultureInfo.CurrentCulture,
                    $"{r.OldClauseCount} → {r.NewClauseCount} clauses · {r.Count(ClauseChangeKind.Modified)} modified · {r.Count(ClauseChangeKind.Renumbered)} renumbered · {r.Count(ClauseChangeKind.Added)} added · {r.Count(ClauseChangeKind.Removed)} removed · {r.Count(ClauseChangeKind.Unchanged)} unchanged");
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void FillClauses()
    {
        Clauses.Clear();
        if (_clauseResult is null)
        {
            return;
        }

        foreach (ClauseComparison c in _clauseResult.Clauses)
        {
            if (ShowUnchangedClauses || c.Kind != ClauseChangeKind.Unchanged)
            {
                Clauses.Add(new ClauseRowViewModel(c));
            }
        }
    }

    partial void OnSelectedClauseChanged(ClauseRowViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        SelectPageRowQuietly(value.Clause.OldPage, value.Clause.NewPage);
        ShowPair(value.Clause.OldPage, value.Clause.NewPage, null);
    }

    [RelayCommand(CanExecute = nameof(HasClauseResult))]
    private void ExportClauseReport()
    {
        if (_clauseResult is { } r && AskReportPath("clause changes") is { } path)
        {
            SaveReport(() => PdfClauseCompare.WriteReport(r, _oldName, _newName, path), path);
        }
    }

    // --- Shared -------------------------------------------------------------------------------

    /// <summary>Outline the change's words that fall on the two pages being shown.</summary>
    private void AddHighlights(VisualDiff diff, int? oldPage, int? newPage, TextHighlight highlight, double dipPerPixel)
    {
        const double pad = 2;
        void Add(DeviceRect? r, bool isOld)
        {
            if (r is { } d)
            {
                Highlights.Add(new CompareHighlightViewModel(
                    (d.X * dipPerPixel) - pad, (d.Y * dipPerPixel) - pad, (d.Width * dipPerPixel) + (2 * pad), (d.Height * dipPerPixel) + (2 * pad), isOld));
            }
        }

        foreach (PageBox b in highlight.Old.Where(b => b.Page == oldPage))
        {
            Add(diff.MapOld(b.Box), isOld: true);
        }

        foreach (PageBox b in highlight.New.Where(b => b.Page == newPage))
        {
            Add(diff.MapNew(b.Box), isOld: false);
        }
    }

    /// <summary>Select the overview row for this pair, if there is one, without re-comparing.</summary>
    private void SelectPageRowQuietly(int? oldPage, int? newPage)
    {
        _suppressPageCompare = true;
        SelectedPage = Pages.FirstOrDefault(p => p.Summary.OldPage == oldPage && p.Summary.NewPage == newPage);
        _suppressPageCompare = false;
    }

    private string? AskReportPath(string what)
    {
        var dialog = new SaveFileDialog
        {
            Title = $"Save {what} report",
            Filter = "HTML report (*.html)|*.html",
            FileName = $"{Path.GetFileNameWithoutExtension(_newName)} {what}.html",
            InitialDirectory = _newDoc.FilePath is { } p ? Path.GetDirectoryName(p) : null,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static void SaveReport(Action write, string path)
    {
        try
        {
            write();
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(ex.Message, "Save report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
