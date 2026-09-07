using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

public enum AnnotationEditorOutcome
{
    Cancel,
    Save,
    Delete,
}

public sealed record AnnotationEditorResult(
    AnnotationEditorOutcome Outcome, string Contents, string ColorKey, IReadOnlyList<PdfReply> Replies);

/// <summary>One already-posted reply in the comment thread.</summary>
public sealed class ThreadMessageViewModel(string? author, DateTimeOffset when, string text)
{
    public string Text { get; } = text;

    public string Attribution
    {
        get
        {
            string who = string.IsNullOrWhiteSpace(author) ? "—" : author.Trim();
            return $"{who} · {when.LocalDateTime:yyyy-MM-dd HH:mm}";
        }
    }
}

/// <summary>Backs the small modal editor for a comment / noted highlight, with its reply thread.</summary>
public sealed partial class AnnotationEditorViewModel : ObservableObject
{
    private readonly List<PdfReply> _replies;
    private readonly string _replyAuthor;

    public AnnotationEditorViewModel(
        PdfAnnotationKind kind, string? contents, string? colorKey, bool canDelete,
        string? author = null, DateTimeOffset? created = null, DateTimeOffset? modified = null,
        IReadOnlyList<PdfReply>? replies = null, string? replyAuthor = null)
    {
        Kind = kind;
        _contents = contents ?? string.Empty;
        _selectedColorKey = colorKey ?? AnnotationColors.Default;
        CanDelete = canDelete;
        Author = string.IsNullOrWhiteSpace(author) ? "—" : author;
        Created = Format(created);
        Modified = Format(modified);
        ShowModified = modified is { } m && created is { } c && (m - c).Duration() > TimeSpan.FromMinutes(1);

        _replies = replies?.ToList() ?? [];
        _replyAuthor = string.IsNullOrWhiteSpace(replyAuthor) ? Environment.UserName : replyAuthor;

        Thread = [];
        RebuildThread();
    }

    public PdfAnnotationKind Kind { get; }

    public string Author { get; }

    /// <summary>Date the annotation was first created, formatted for display (or "—").</summary>
    public string Created { get; }

    public string Modified { get; }

    /// <summary>Whether to show the "edited" line (the annotation has been changed since creation).</summary>
    public bool ShowModified { get; }

    private static string Format(DateTimeOffset? value) =>
        value is { } v ? v.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : "—";

    public bool IsHighlight => Kind == PdfAnnotationKind.Highlight;

    public bool CanDelete { get; }

    public string Title => Kind == PdfAnnotationKind.Highlight ? "Highlight note" : "Comment";

    public IReadOnlyList<string> Colors { get; } = AnnotationColors.Keys;

    /// <summary>The conversation so far: the opening comment then every reply, oldest first.</summary>
    public ObservableCollection<ThreadMessageViewModel> Thread { get; }

    /// <summary>Whether to show the thread panel — only once at least one reply exists.</summary>
    public bool HasThread => _replies.Count > 0;

    [ObservableProperty]
    private string _contents;

    [ObservableProperty]
    private string _selectedColorKey;

    [ObservableProperty]
    private string _newReply = string.Empty;

    /// <summary>Set by the host; invoked with the outcome.</summary>
    public Action<AnnotationEditorResult>? Closed { get; set; }

    [RelayCommand]
    private void AddReply()
    {
        string text = NewReply.Trim();
        if (text.Length == 0)
        {
            return;
        }

        _replies.Add(PdfReply.New(text, _replyAuthor));
        NewReply = string.Empty;
        OnPropertyChanged(nameof(HasThread));
        RebuildThread();
    }

    private void RebuildThread()
    {
        // The opening message is the editable box above with its own attribution line — the
        // thread lists just the replies to it.
        Thread.Clear();
        foreach (PdfReply reply in _replies)
        {
            Thread.Add(new ThreadMessageViewModel(reply.Author, reply.Created, reply.Text));
        }
    }

    [RelayCommand]
    private void Save() =>
        Closed?.Invoke(new AnnotationEditorResult(
            AnnotationEditorOutcome.Save, Contents.Trim(), SelectedColorKey, _replies.ToArray()));

    [RelayCommand]
    private void Cancel() =>
        Closed?.Invoke(new AnnotationEditorResult(
            AnnotationEditorOutcome.Cancel, Contents, SelectedColorKey, _replies.ToArray()));

    [RelayCommand]
    private void Delete() =>
        Closed?.Invoke(new AnnotationEditorResult(
            AnnotationEditorOutcome.Delete, Contents, SelectedColorKey, _replies.ToArray()));
}
