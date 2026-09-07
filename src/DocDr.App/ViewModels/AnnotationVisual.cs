using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>
/// One annotation projected into a page slot's DIP space, ready for the overlay to draw.
/// <see cref="Rects"/> are the highlight bands (empty for a bare comment or ink);
/// <see cref="Strokes"/> are the ink polylines; <see cref="Marker"/> is where the note glyph
/// or delete button goes.
/// </summary>
public sealed record AnnotationVisual(
    System.Guid Id,
    PdfAnnotationKind Kind,
    IReadOnlyList<Rect> Rects,
    Rect Marker,
    Color Color,
    bool HasNote,
    bool IsSelected)
{
    /// <summary>Ink polylines in the slot's DIP space (empty unless <see cref="Kind"/> is Ink).</summary>
    public IReadOnlyList<PointCollection> Strokes { get; init; } = [];

    /// <summary>Ink stroke thickness in DIP.</summary>
    public double StrokeThickness { get; init; }

    public Brush FillBrush => new SolidColorBrush(Color) { Opacity = 0.4 };

    /// <summary>Highlighter ink — the colour at marker-pen translucency.</summary>
    public Brush StrokeBrush => new SolidColorBrush(Color) { Opacity = 0.45 };

    public bool ShowMarker => Kind == PdfAnnotationKind.Comment || HasNote;

    /// <summary>A selected highlight / ink gets an inline trash button (comments use the marker).</summary>
    public bool ShowDeleteButton => IsSelected && Kind is PdfAnnotationKind.Highlight or PdfAnnotationKind.Ink;
}
