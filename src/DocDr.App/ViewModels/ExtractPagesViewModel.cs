using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>
/// Backs the "Extract pages" dialog: which pages (a range the user can edit, pre-filled from the
/// current page, the thumbnail selection, or a bookmark's chapter), a title for the new file, and
/// whether to carry annotations / bookmarks across.
/// </summary>
public sealed partial class ExtractPagesViewModel : ObservableObject
{
    private readonly int _pageCount;

    public ExtractPagesViewModel(IReadOnlyList<int> initialPages, int pageCount, string title, bool hasAnnotations)
    {
        _pageCount = pageCount;
        _rangeText = PdfPageRange.Format(initialPages).Replace(",", ", ", StringComparison.Ordinal);
        _title = title;
        HasAnnotations = hasAnnotations;
        _includeAnnotations = hasAnnotations;
        Validate();
    }

    public string PageCountText => $"Page positions in the file, 1–{_pageCount}. e.g. 142  or  150-152, 160";

    public bool HasAnnotations { get; }

    [ObservableProperty]
    private string _rangeText;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private bool _includeAnnotations;

    [ObservableProperty]
    private bool _includeBookmarks = true;

    [ObservableProperty]
    private bool _openAfter;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExtractCommand))]
    private string _error = string.Empty;

    /// <summary>"3 pages" — what the current range selects.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    public IReadOnlyList<int> Pages { get; private set; } = [];

    public bool Confirmed { get; private set; }

    public Action? Close { get; set; }

    partial void OnRangeTextChanged(string value) => Validate();

    private void Validate()
    {
        if (PdfPageRange.TryParse(RangeText, _pageCount, out IReadOnlyList<int> pages, out string error))
        {
            Pages = pages;
            Error = string.Empty;
            Summary = pages.Count == 1 ? "1 page" : $"{pages.Count} pages";
        }
        else
        {
            Pages = [];
            Error = error;
            Summary = string.Empty;
        }
    }

    private bool CanExtract() => Error.Length == 0 && Pages.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExtract))]
    private void Extract()
    {
        Confirmed = true;
        Close?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => Close?.Invoke();
}
