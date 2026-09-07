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

    /// <summary>The box rectangle in slot DIP space for a text box / callout / cloud.</summary>
    public Rect Box { get; init; }

    /// <summary>The callout leader polyline (tip first) in slot DIP space; empty otherwise.</summary>
    public PointCollection Leader { get; init; } = [];

    /// <summary>The open arrowhead at the callout leader's tip (base, tip, base) in slot DIP space.</summary>
    public PointCollection LeaderArrow { get; init; } = [];

    /// <summary>The scalloped cloud outline in slot DIP space; null unless <see cref="Kind"/> is Cloud.</summary>
    public Geometry? CloudGeometry { get; init; }

    /// <summary>Text shown inside a text box / callout.</summary>
    public string BoxText { get; init; } = string.Empty;

    /// <summary>Wrapping width for the box text (box width minus the border/padding), in DIP.</summary>
    public double BoxTextWidth => Math.Max(1, Box.Width - 11);

    /// <summary>Text size in DIP for a text box / callout.</summary>
    public double BoxFontSize { get; init; }

    public Brush FillBrush => new SolidColorBrush(Color) { Opacity = 0.4 };

    /// <summary>Highlighter ink — the colour at marker-pen translucency.</summary>
    public Brush StrokeBrush => new SolidColorBrush(Color) { Opacity = 0.45 };

    /// <summary>Opaque colour brush — the border/text colour for the shape kinds.</summary>
    public Brush ShapeBrush => new SolidColorBrush(Color);

    public bool IsTextBox => Kind is PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout;

    public bool IsCallout => Kind == PdfAnnotationKind.Callout;

    public bool IsCloud => Kind == PdfAnnotationKind.Cloud;

    public bool ShowMarker => Kind == PdfAnnotationKind.Comment || (HasNote && !IsTextBox);

    /// <summary>A selected highlight / ink / shape gets an inline trash button (comments use the marker).</summary>
    public bool ShowDeleteButton => IsSelected && Kind is PdfAnnotationKind.Highlight
        or PdfAnnotationKind.Ink or PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout or PdfAnnotationKind.Cloud;
}
