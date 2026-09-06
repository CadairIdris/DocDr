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

    public string Kind => annotation.Kind == PdfAnnotationKind.Highlight ? "Highlight" : "Comment";

    public Brush Swatch { get; } = new SolidColorBrush(AnnotationColors.ToColor(annotation.ColorArgb));

    public bool IsComment => annotation.Kind == PdfAnnotationKind.Comment;

    public string Text => string.IsNullOrWhiteSpace(annotation.Contents)
        ? (annotation.Kind == PdfAnnotationKind.Highlight ? "(highlight)" : "(empty note)")
        : annotation.Contents.ReplaceLineEndings(" ").Trim();
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
