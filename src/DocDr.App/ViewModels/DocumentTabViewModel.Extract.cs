using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Views;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>
/// Extract pages: save some pages of this document as a new PDF — from Pages ▾, the thumbnails'
/// right-click menu (the selection), or a bookmark's "Extract chapter…" (its chapter's range).
/// </summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>Open a saved extract in a new tab (handled by <see cref="MainViewModel"/>).</summary>
    public event Action<string>? OpenFileRequested;

    /// <summary>Pages ▾ / thumbnails: the thumbnail selection if there is one, else the current page.</summary>
    [RelayCommand]
    private void ExtractPages()
    {
        IReadOnlyList<int> pages = Thumbnails.HasSelection
            ? Thumbnails.SelectedPageIndices
            : [Math.Clamp(LeftPane.CurrentPage - 1, 0, Math.Max(0, Document.PageCount - 1))];
        OpenExtractDialog(pages, suggestedTitle: null);
    }

    private void OnBookmarkExtractRequested(BookmarkNodeViewModel node)
    {
        if (BookmarksViewModel.ChapterRange(Bookmarks.Roots, node, Document.PageCount) is not { } range)
        {
            MessageBox.Show("That bookmark doesn't point at a page.", "Extract chapter",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenExtractDialog(Enumerable.Range(range.Start, range.End - range.Start + 1).ToArray(), node.Title.Trim());
    }

    private async void OpenExtractDialog(IReadOnlyList<int> initialPages, string? suggestedTitle)
    {
        if (Document.PageCount == 0)
        {
            return;
        }

        string docTitle = string.IsNullOrWhiteSpace(Document.Info.Title)
            ? Path.GetFileNameWithoutExtension(_baseTitle)
            : Document.Info.Title.Trim();
        var vm = new ExtractPagesViewModel(initialPages, Document.PageCount,
            suggestedTitle is { Length: > 0 } t ? $"{docTitle} — {t}" : docTitle,
            Document.HasAnnotations);
        var window = new ExtractPagesWindow(vm) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();
        if (!vm.Confirmed || vm.Pages.Count == 0)
        {
            return;
        }

        string stem = Path.GetFileNameWithoutExtension(_baseTitle);
        string what = suggestedTitle is { Length: > 0 } chapter ? chapter : "p" + PdfPageRange.Format(vm.Pages);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save extracted pages",
            Filter = "PDF (*.pdf)|*.pdf",
            FileName = SafeFileName($"{stem} - {what}") + ".pdf",
        };
        if (Document.FilePath is { } source && Path.GetDirectoryName(source) is { Length: > 0 } dir)
        {
            dialog.InitialDirectory = dir;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (Document.FilePath is { } original
            && string.Equals(Path.GetFullPath(original), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Choose a different file name — that's the document you're extracting from.",
                "Extract pages", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var options = new PdfExtractOptions
        {
            IncludeAnnotations = vm.IncludeAnnotations,
            IncludeBookmarks = vm.IncludeBookmarks,
            Title = string.IsNullOrWhiteSpace(vm.Title) ? null : vm.Title.Trim(),
        };
        IReadOnlyList<int> pages = vm.Pages;
        string path = dialog.FileName;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            byte[] bytes = await Task.Run(() => Document.ExtractPages(pages, options));
            await File.WriteAllBytesAsync(path, bytes);
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show($"Could not extract the pages: {ex.Message}", "Extract pages",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (vm.OpenAfter)
        {
            OpenFileRequested?.Invoke(path);
        }
    }

    private static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim();
        return cleaned.Length > 120 ? cleaned[..120].TrimEnd() : cleaned;
    }
}
