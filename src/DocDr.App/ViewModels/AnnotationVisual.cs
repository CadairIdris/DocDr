using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>
/// One annotation projected into a page slot's DIP space, ready for the overlay to draw.
/// <see cref="Rects"/> are the highlight bands (empty for a bare comment); <see cref="Marker"/>
/// is where the note glyph goes.
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
    public Brush FillBrush => new SolidColorBrush(Color) { Opacity = 0.4 };

    public bool ShowMarker => Kind == PdfAnnotationKind.Comment || HasNote;

    /// <summary>A selected highlight gets an inline trash button (comments are edited/deleted via the marker).</summary>
    public bool ShowDeleteButton => IsSelected && Kind == PdfAnnotationKind.Highlight;
}
