using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.Pdf;
using Microsoft.Win32;

namespace DocDr.App.ViewModels;

/// <summary>One file in the merge list: its path, page count, and an editable bookmark title.</summary>
public sealed partial class MergeItemViewModel : ObservableObject
{
    public MergeItemViewModel(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        _title = System.IO.Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('-', ' ').Trim();
    }

    public string Path { get; }

    public string FileName { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private int? _pageCount;
}

/// <summary>One merged file's chosen order and bookmark title.</summary>
public sealed record MergeFile(string Path, string Title);

/// <summary>The confirmed merge: ordered files and whether to add a bookmark per file.</summary>
public sealed record MergeRequest(IReadOnlyList<MergeFile> Files, bool AddBookmarks);

/// <summary>Backs the "Merge PDFs" dialog: a reorderable, renamable file list.</summary>
public sealed partial class MergeViewModel : ObservableObject
{
    public MergeViewModel(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            Files.Add(new MergeItemViewModel(path));
        }

        Files.CollectionChanged += (_, _) =>
        {
            MergeCommand.NotifyCanExecuteChanged();
            MoveUpCommand.NotifyCanExecuteChanged();
            MoveDownCommand.NotifyCanExecuteChanged();
        };
        _ = LoadDetailsAsync();
    }

    public ObservableCollection<MergeItemViewModel> Files { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private MergeItemViewModel? _selectedFile;

    [ObservableProperty]
    private bool _addBookmarks = true;

    [ObservableProperty]
    private bool _isBusy = true;

    /// <summary>Set by the host; invoked with the request on OK, or null on cancel.</summary>
    public Action<MergeRequest?>? Confirmed { get; set; }

    /// <summary>Raised after a reorder so the view can keep the moved row visible.</summary>
    public event Action<MergeItemViewModel>? ScrollToItem;

    private async Task LoadDetailsAsync()
    {
        foreach (MergeItemViewModel item in Files.ToArray())
        {
            (int pages, string title) = await Task.Run(() =>
            {
                try
                {
                    using PdfDocument doc = PdfDocument.Load(item.Path);
                    return (doc.PageCount, PdfHeadings.SuggestTitle(doc, item.Path));
                }
                catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
                {
                    return (0, item.Title);
                }
            }).ConfigureAwait(true);

            item.PageCount = pages;
            item.Title = title;
        }

        IsBusy = false;
    }

    private bool CanMove(int direction) =>
        SelectedFile is not null &&
        Files.IndexOf(SelectedFile) + direction is var i && i >= 0 && i < Files.Count;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    private bool CanMoveUp() => CanMove(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private bool CanMoveDown() => CanMove(1);

    private void Move(int direction)
    {
        if (SelectedFile is null)
        {
            return;
        }

        MergeItemViewModel moved = SelectedFile;
        int i = Files.IndexOf(moved);
        Files.Move(i, i + direction);
        SelectedFile = moved;
        ScrollToItem?.Invoke(moved);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (SelectedFile is { } item)
        {
            Files.Remove(item);
        }
    }

    private bool HasSelection() => SelectedFile is not null;

    [RelayCommand]
    private void AddFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add PDFs",
            Filter = "PDF documents (*.pdf)|*.pdf",
            CheckFileExists = true,
            Multiselect = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var existing = Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<MergeItemViewModel>();
        foreach (string path in dialog.FileNames.Where(p => existing.Add(p)))
        {
            var item = new MergeItemViewModel(path);
            Files.Add(item);
            added.Add(item);
        }

        _ = FillAsync(added);
    }

    private async Task FillAsync(IReadOnlyList<MergeItemViewModel> items)
    {
        foreach (MergeItemViewModel item in items)
        {
            (int pages, string title) = await Task.Run(() =>
            {
                try
                {
                    using PdfDocument doc = PdfDocument.Load(item.Path);
                    return (doc.PageCount, PdfHeadings.SuggestTitle(doc, item.Path));
                }
                catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
                {
                    return (0, item.Title);
                }
            }).ConfigureAwait(true);

            item.PageCount = pages;
            item.Title = title;
        }
    }

    public bool CanMerge => Files.Count >= 2;

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private void Merge() => Confirmed?.Invoke(new MergeRequest(
        Files.Select(f => new MergeFile(f.Path, string.IsNullOrWhiteSpace(f.Title) ? f.FileName : f.Title.Trim())).ToArray(),
        AddBookmarks));

    [RelayCommand]
    private void Cancel() => Confirmed?.Invoke(null);
}
