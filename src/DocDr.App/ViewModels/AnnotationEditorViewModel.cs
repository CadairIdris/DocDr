using System;
using System.Collections.Generic;
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

public sealed record AnnotationEditorResult(AnnotationEditorOutcome Outcome, string Contents, string ColorKey);

/// <summary>Backs the small modal editor for a comment / noted highlight.</summary>
public sealed partial class AnnotationEditorViewModel : ObservableObject
{
    public AnnotationEditorViewModel(
        PdfAnnotationKind kind, string? contents, string? colorKey, bool canDelete,
        string? author = null, DateTimeOffset? created = null, DateTimeOffset? modified = null)
    {
        Kind = kind;
        _contents = contents ?? string.Empty;
        _selectedColorKey = colorKey ?? AnnotationColors.Default;
        CanDelete = canDelete;
        Author = string.IsNullOrWhiteSpace(author) ? "—" : author;
        Created = Format(created);
        Modified = Format(modified);
        ShowModified = modified is { } m && created is { } c && (m - c).Duration() > TimeSpan.FromMinutes(1);
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

    [ObservableProperty]
    private string _contents;

    [ObservableProperty]
    private string _selectedColorKey;

    /// <summary>Set by the host; invoked with the outcome.</summary>
    public Action<AnnotationEditorResult>? Closed { get; set; }

    [RelayCommand]
    private void Save() =>
        Closed?.Invoke(new AnnotationEditorResult(AnnotationEditorOutcome.Save, Contents.Trim(), SelectedColorKey));

    [RelayCommand]
    private void Cancel() =>
        Closed?.Invoke(new AnnotationEditorResult(AnnotationEditorOutcome.Cancel, Contents, SelectedColorKey));

    [RelayCommand]
    private void Delete() =>
        Closed?.Invoke(new AnnotationEditorResult(AnnotationEditorOutcome.Delete, Contents, SelectedColorKey));
}
