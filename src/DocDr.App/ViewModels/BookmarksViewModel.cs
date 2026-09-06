using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>One node of the document outline shown in the bookmarks tree.</summary>
public sealed partial class BookmarkNodeViewModel : ObservableObject
{
    public BookmarkNodeViewModel(PdfBookmark bookmark, int depth)
    {
        Title = string.IsNullOrWhiteSpace(bookmark.Title) ? "(untitled)" : bookmark.Title;
        PageIndex = bookmark.PageIndex;
        Children = new ObservableCollection<BookmarkNodeViewModel>(
            bookmark.Children.Select(c => new BookmarkNodeViewModel(c, depth + 1)));
        _isExpanded = depth < 1; // top level open, deeper levels collapsed
    }

    public string Title { get; }

    public int? PageIndex { get; }

    public string PageLabel => PageIndex is int p ? (p + 1).ToString() : string.Empty;

    public bool HasTarget => PageIndex is not null;

    public ObservableCollection<BookmarkNodeViewModel> Children { get; }

    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>The document outline for a tab, plus jump-to-page activation.</summary>
public sealed partial class BookmarksViewModel : ObservableObject
{
    public BookmarksViewModel(IReadOnlyList<PdfBookmark> bookmarks)
    {
        Roots = [];
        Reload(bookmarks);
    }

    public ObservableCollection<BookmarkNodeViewModel> Roots { get; }

    public bool HasBookmarks => Roots.Count > 0;

    /// <summary>Rebuild the tree after the document's outline changed (pages inserted / deleted).</summary>
    public void Reload(IReadOnlyList<PdfBookmark> bookmarks)
    {
        Roots.Clear();
        foreach (PdfBookmark bookmark in bookmarks)
        {
            Roots.Add(new BookmarkNodeViewModel(bookmark, 0));
        }

        OnPropertyChanged(nameof(HasBookmarks));
    }

    /// <summary>Raised when a bookmark with a target is chosen; carries the 0-based page index.</summary>
    public event Action<int>? BookmarkActivated;

    public void Activate(BookmarkNodeViewModel? node)
    {
        if (node?.PageIndex is int pageIndex)
        {
            BookmarkActivated?.Invoke(pageIndex);
        }
    }
}
