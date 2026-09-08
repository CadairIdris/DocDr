using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>One row in the navigation panel's Annotations list.</summary>
public sealed class AnnotationRowViewModel(int pageIndex, PdfAnnotation annotation)
{
    public Guid Id { get; } = annotation.Id;

    public int PageIndex { get; } = pageIndex;

    public string PageLabel => $"p. {PageIndex + 1}";

    /// <summary>A friendly kind label, always shown.</summary>
    public string Kind => AnnotationKinds.Label(annotation.Kind);

    public Brush Swatch { get; } = new SolidColorBrush(AnnotationColors.ToColor(annotation.ColorArgb));

    /// <summary>The annotation's text (comment / note / box text), collapsed to one line; empty if none.</summary>
    public string Text => string.IsNullOrWhiteSpace(annotation.Contents)
        ? string.Empty
        : annotation.Contents.ReplaceLineEndings(" ").Trim();

    public bool HasText => Text.Length > 0;

    /// <summary>"author · date" for the annotation, or just one of them, or empty.</summary>
    public string Attribution
    {
        get
        {
            string? who = string.IsNullOrWhiteSpace(annotation.Author) ? null : annotation.Author.Trim();
            string? when = (annotation.Created ?? annotation.Modified) is { } d
                ? d.LocalDateTime.ToString("yyyy-MM-dd")
                : null;
            return (who, when) switch
            {
                (not null, not null) => $"{who} · {when}",
                (not null, null) => who,
                (null, not null) => when,
                _ => string.Empty,
            };
        }
    }

    public bool HasAttribution => Attribution.Length > 0;

    public int ReplyCount { get; } = annotation.Replies.Count;

    public bool HasReplies => ReplyCount > 0;

    public string ReplyBadge => ReplyCount == 1 ? "1 reply" : $"{ReplyCount} replies";
}

/// <summary>Flat list of every annotation in the document, for the nav panel Annotations tab.</summary>
public sealed partial class AnnotationListViewModel : ObservableObject
{
    public ObservableCollection<AnnotationRowViewModel> Rows { get; } = [];

    public bool HasAnnotations => Rows.Count > 0;

    /// <summary>Raised when a row is chosen; carries the page index and the annotation id.</summary>
    public event Action<int, Guid>? AnnotationActivated;

    public void Reload(PdfDocument document)
    {
        Rows.Clear();
        for (int page = 0; page < document.PageCount; page++)
        {
            foreach (PdfAnnotation annotation in document.GetAnnotations(page))
            {
                Rows.Add(new AnnotationRowViewModel(page, annotation));
            }
        }

        OnPropertyChanged(nameof(HasAnnotations));
    }

    public void Activate(AnnotationRowViewModel? row)
    {
        if (row is not null)
        {
            AnnotationActivated?.Invoke(row.PageIndex, row.Id);
        }
    }
}
