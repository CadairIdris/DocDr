using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>One node of the document outline shown in the bookmarks tree.</summary>
public sealed partial class BookmarkNodeViewModel : ObservableObject
{
    public BookmarkNodeViewModel(PdfBookmark bookmark, int depth, Func<int, string> pageLabel)
    {
        _title = string.IsNullOrWhiteSpace(bookmark.Title) ? "(untitled)" : bookmark.Title;
        PageIndex = bookmark.PageIndex;
        PageLabel = bookmark.PageIndex is int p ? pageLabel(p) : string.Empty;
        Children = new ObservableCollection<BookmarkNodeViewModel>(
            bookmark.Children.Select(c => new BookmarkNodeViewModel(c, depth + 1, pageLabel)));
        _isExpanded = depth < 1; // top level open, deeper levels collapsed
    }

    [ObservableProperty]
    private string _title;

    public int? PageIndex { get; }

    public string PageLabel { get; }

    public bool HasTarget => PageIndex is not null;

    public ObservableCollection<BookmarkNodeViewModel> Children { get; }

    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>True while the title is being edited inline.</summary>
    [ObservableProperty]
    private bool _isEditing;

    public PdfBookmark ToBookmark() => new(
        string.IsNullOrWhiteSpace(Title) ? string.Empty : Title.Trim(),
        PageIndex,
        Children.Select(c => c.ToBookmark()).ToArray());
}

/// <summary>The document outline for a tab: jump-to-page, plus inline rename / delete.</summary>
public sealed partial class BookmarksViewModel : ObservableObject
{
    private Func<int, string> _pageLabel = PageDisplay.Ordinal;

    public BookmarksViewModel(IReadOnlyList<PdfBookmark> bookmarks)
    {
        Roots = [];
        Reload(bookmarks);
    }

    public ObservableCollection<BookmarkNodeViewModel> Roots { get; }

    public bool HasBookmarks => Roots.Count > 0;

    /// <summary>Rebuild the tree from a new outline (generated, or pages inserted / deleted).</summary>
    public void Reload(IReadOnlyList<PdfBookmark> bookmarks, Func<int, string>? pageLabel = null)
    {
        _pageLabel = pageLabel ?? _pageLabel;
        Roots.Clear();
        foreach (PdfBookmark bookmark in bookmarks)
        {
            Roots.Add(new BookmarkNodeViewModel(bookmark, 0, _pageLabel));
        }

        OnPropertyChanged(nameof(HasBookmarks));
    }

    /// <summary>Raised when a bookmark with a target is chosen; carries the 0-based page index.</summary>
    public event Action<int>? BookmarkActivated;

    /// <summary>"Extract chapter…" on a bookmark: the tab works out its page range and opens the
    /// Extract pages dialog.</summary>
    public event Action<BookmarkNodeViewModel>? ExtractRequested;

    public void RequestExtract(BookmarkNodeViewModel node) => ExtractRequested?.Invoke(node);

    /// <summary>
    /// The pages a bookmark's chapter spans: from its own page up to the page before the next
    /// bookmark (in reading order) that isn't inside it and starts on a later page — so a chapter
    /// includes its sub-sections, and a section ends where the next sibling or the next chapter
    /// begins. The last chapter runs to the end of the document. Null for a bookmark with no target.
    /// </summary>
    public static (int Start, int End)? ChapterRange(
        IReadOnlyList<BookmarkNodeViewModel> roots, BookmarkNodeViewModel node, int pageCount)
    {
        if (node.PageIndex is not int start || pageCount <= 0)
        {
            return null;
        }

        start = Math.Clamp(start, 0, pageCount - 1);
        var order = new List<BookmarkNodeViewModel>();
        void Walk(IEnumerable<BookmarkNodeViewModel> nodes)
        {
            foreach (BookmarkNodeViewModel n in nodes)
            {
                order.Add(n);
                Walk(n.Children);
            }
        }

        Walk(roots);
        int at = order.IndexOf(node);
        if (at < 0)
        {
            return (start, pageCount - 1);
        }

        int after = at + 1 + CountDescendants(node);
        for (int i = after; i < order.Count; i++)
        {
            if (order[i].PageIndex is int next && next > start)
            {
                return (start, Math.Min(next - 1, pageCount - 1));
            }
        }

        return (start, pageCount - 1);

        static int CountDescendants(BookmarkNodeViewModel n)
        {
            int total = 0;
            foreach (BookmarkNodeViewModel c in n.Children)
            {
                total += 1 + CountDescendants(c);
            }

            return total;
        }
    }

    /// <summary>Raised after the user renames or deletes a bookmark; carries the new tree to persist.</summary>
    public event Action<IReadOnlyList<PdfBookmark>>? OutlineEdited;

    public void Activate(BookmarkNodeViewModel? node)
    {
        if (node?.PageIndex is int pageIndex && !node.IsEditing)
        {
            BookmarkActivated?.Invoke(pageIndex);
        }
    }

    /// <summary>Called by the view when an inline edit ends. Persists only if the title changed.</summary>
    public void CommitRename(BookmarkNodeViewModel node, bool changed)
    {
        node.IsEditing = false;
        if (string.IsNullOrWhiteSpace(node.Title))
        {
            node.Title = "(untitled)";
        }

        if (changed)
        {
            RaiseEdited();
        }
    }

    [RelayCommand]
    private void DeleteNode(BookmarkNodeViewModel? node)
    {
        if (node is not null && RemoveFrom(Roots, node))
        {
            OnPropertyChanged(nameof(HasBookmarks));
            RaiseEdited();
        }
    }

    private static bool RemoveFrom(ObservableCollection<BookmarkNodeViewModel> list, BookmarkNodeViewModel target)
    {
        if (list.Remove(target))
        {
            return true;
        }

        foreach (BookmarkNodeViewModel child in list)
        {
            if (RemoveFrom(child.Children, target))
            {
                return true;
            }
        }

        return false;
    }

    private void RaiseEdited() =>
        OutlineEdited?.Invoke(Roots.Select(r => r.ToBookmark()).ToArray());
}
