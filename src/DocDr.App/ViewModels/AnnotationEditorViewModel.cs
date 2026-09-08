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
    AnnotationEditorOutcome Outcome, string Contents, string ColorKey, IReadOnlyList<PdfReply> Replies)
{
    /// <summary>Text size in points for a text box / callout (ignored by other kinds).</summary>
    public double FontSize { get; init; } = PdfAnnotation.DefaultFontSize;
}

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
        IReadOnlyList<PdfReply>? replies = null, string? replyAuthor = null,
        double fontSize = 0)
    {
        Kind = kind;
        _contents = contents ?? string.Empty;
        _selectedColorKey = colorKey ?? AnnotationColors.Default;
        _fontSizeText = ((int)Math.Round(fontSize > 0 ? fontSize : PdfAnnotation.DefaultFontSize))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>Whether this is a text box / callout — a text-only editor (colour / size / border are on the format bar).</summary>
    public bool IsTextShape => Kind is PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout;

    /// <summary>Colour swatches show for highlights only; shapes / text boxes use the floating format toolbar.</summary>
    public bool ShowColors => IsHighlight;

    /// <summary>Only comments and noted highlights carry a conversation — a text box / callout does not.</summary>
    public bool AllowReplies => Kind is PdfAnnotationKind.Comment or PdfAnnotationKind.Highlight;

    public bool CanDelete { get; }

    public string Title => Kind switch
    {
        PdfAnnotationKind.Highlight => "Highlight note",
        PdfAnnotationKind.TextBox => "Text box",
        PdfAnnotationKind.Callout => "Callout",
        _ => "Note",
    };

    public IReadOnlyList<string> Colors { get; } = AnnotationColors.PresetKeys;

    [ObservableProperty]
    private string _fontSizeText;

    private double FontSize =>
        double.TryParse(FontSizeText, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out double f) && f > 0
            ? f
            : PdfAnnotation.DefaultFontSize;

    /// <summary>The conversation so far: the opening comment then every reply, oldest first.</summary>
    public ObservableCollection<ThreadMessageViewModel> Thread { get; }

    /// <summary>Whether to show the thread panel — replies allowed for this kind, and at least one posted.</summary>
    public bool ShowThread => AllowReplies && _replies.Count > 0;

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
        OnPropertyChanged(nameof(ShowThread));
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
    private void Save()
    {
        AddReply(); // commit any text still sitting in the reply box
        Closed?.Invoke(new AnnotationEditorResult(
            AnnotationEditorOutcome.Save, Contents.Trim(), SelectedColorKey, _replies.ToArray())
            { FontSize = FontSize });
    }

    [RelayCommand]
    private void Cancel() =>
        Closed?.Invoke(new AnnotationEditorResult(
            AnnotationEditorOutcome.Cancel, Contents, SelectedColorKey, _replies.ToArray()));

    [RelayCommand]
    private void Delete() =>
        Closed?.Invoke(new AnnotationEditorResult(
            AnnotationEditorOutcome.Delete, Contents, SelectedColorKey, _replies.ToArray()));
}
