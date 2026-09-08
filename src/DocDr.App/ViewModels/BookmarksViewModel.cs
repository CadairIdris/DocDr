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
    public BookmarkNodeViewModel(PdfBookmark bookmark, int depth)
    {
        _title = string.IsNullOrWhiteSpace(bookmark.Title) ? "(untitled)" : bookmark.Title;
        PageIndex = bookmark.PageIndex;
        Children = new ObservableCollection<BookmarkNodeViewModel>(
            bookmark.Children.Select(c => new BookmarkNodeViewModel(c, depth + 1)));
        _isExpanded = depth < 1; // top level open, deeper levels collapsed
    }

    [ObservableProperty]
    private string _title;

    public int? PageIndex { get; }

    public string PageLabel => PageIndex is int p ? (p + 1).ToString() : string.Empty;

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
    public BookmarksViewModel(IReadOnlyList<PdfBookmark> bookmarks)
    {
        Roots = [];
        Reload(bookmarks);
    }

    public ObservableCollection<BookmarkNodeViewModel> Roots { get; }

    public bool HasBookmarks => Roots.Count > 0;

    /// <summary>Rebuild the tree from a new outline (generated, or pages inserted / deleted).</summary>
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

    /// <summary>Raised after the user renames or deletes a bookmark; carries the new tree to persist.</summary>
    public event Action<IReadOnlyList<PdfBookmark>>? OutlineEdited;

    public void Activate(BookmarkNodeViewModel? node)
    {
        if (node?.PageIndex is int pageIndex && !node.IsEditing)
        {
            BookmarkActivated?.Invoke(pageIndex);
        }
    }

    [RelayCommand]
    private void BeginRename(BookmarkNodeViewModel? node)
    {
        if (node is not null)
        {
            node.IsEditing = true;
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
