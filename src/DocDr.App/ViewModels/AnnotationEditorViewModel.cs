using System;
using System.Collections.Generic;
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

/// <summary>
/// Backs the small modal editor for a note / noted highlight / text box. Text (and a highlight's
/// colour) only — the reply thread, review status and checkmark live in the Comments panel. The
/// annotation's existing replies are passed through untouched so saving never drops them.
/// </summary>
public sealed partial class AnnotationEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<PdfReply> _replies;

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

        _replies = replies?.ToArray() ?? [];
        _ = replyAuthor; // kept for call-site compatibility; replies are posted from the Comments panel
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

    [ObservableProperty]
    private string _contents;

    [ObservableProperty]
    private string _selectedColorKey;

    /// <summary>Set by the host; invoked with the outcome.</summary>
    public Action<AnnotationEditorResult>? Closed { get; set; }

    [RelayCommand]
    private void Save()
    {
        Closed?.Invoke(new AnnotationEditorResult(
            AnnotationEditorOutcome.Save, Contents.Trim(), SelectedColorKey, _replies)
            { FontSize = FontSize });
    }

    [RelayCommand]
    private void Cancel() =>
        Closed?.Invoke(new AnnotationEditorResult(
            AnnotationEditorOutcome.Cancel, Contents, SelectedColorKey, _replies));

    [RelayCommand]
    private void Delete() =>
        Closed?.Invoke(new AnnotationEditorResult(
            AnnotationEditorOutcome.Delete, Contents, SelectedColorKey, _replies));
}
