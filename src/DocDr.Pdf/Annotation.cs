namespace DocDr.Pdf;

/// <summary>The annotation kinds DocDr authors and round-trips.</summary>
public enum PdfAnnotationKind
{
    /// <summary>A text-markup highlight over one or more runs of text (PDF subtype <c>Highlight</c>).</summary>
    Highlight,

    /// <summary>A sticky-note comment anchored to a point (PDF subtype <c>Text</c>).</summary>
    Comment,

    /// <summary>A freehand highlighter drawing — one or more polylines (PDF subtype <c>Ink</c>).</summary>
    Ink,
}

/// <summary>
/// One DocDr-managed annotation on a logical page. Immutable; edits produce a new record with the
/// same <see cref="Id"/>. Geometry is in <b>unrotated</b> PDFium page space (points, bottom-left
/// origin) — the same space <c>FPDFText_GetRect</c> and annotation quad points use.
/// </summary>
public sealed record PdfAnnotation(
    Guid Id,
    PdfAnnotationKind Kind,
    IReadOnlyList<PdfRect> Quads,
    uint ColorArgb,
    string? Contents,
    string? Author,
    DateTimeOffset? Created,
    DateTimeOffset? Modified)
{
    /// <summary>Freehand ink strokes (each a polyline in unrotated page space). Empty unless <see cref="Kind"/> is Ink.</summary>
    public IReadOnlyList<IReadOnlyList<PdfPoint>> Strokes { get; init; } = [];

    /// <summary>Ink stroke width in points.</summary>
    public double StrokeWidth { get; init; }

    /// <summary>PDF annotation subtype number for <see cref="Kind"/> (matches the PDF spec / PDFium).</summary>
    public int Subtype => Kind switch
    {
        PdfAnnotationKind.Highlight => 9,
        PdfAnnotationKind.Ink => 15,
        _ => 1,
    };

    /// <summary>Smallest rectangle covering every quad / stroke point.</summary>
    public PdfRect Bounds
    {
        get
        {
            double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;

            foreach (PdfRect q in Quads)
            {
                left = Math.Min(left, Math.Min(q.Left, q.Right));
                right = Math.Max(right, Math.Max(q.Left, q.Right));
                bottom = Math.Min(bottom, Math.Min(q.Top, q.Bottom));
                top = Math.Max(top, Math.Max(q.Top, q.Bottom));
            }

            foreach (IReadOnlyList<PdfPoint> stroke in Strokes)
            {
                foreach (PdfPoint p in stroke)
                {
                    left = Math.Min(left, p.X);
                    right = Math.Max(right, p.X);
                    bottom = Math.Min(bottom, p.Y);
                    top = Math.Max(top, p.Y);
                }
            }

            if (left > right || bottom > top)
            {
                return default;
            }

            double pad = Kind == PdfAnnotationKind.Ink ? Math.Max(1, StrokeWidth) : 0;
            return new PdfRect(left - pad, top + pad, right + pad, bottom - pad);
        }
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(Contents);

    public static PdfAnnotation NewHighlight(IReadOnlyList<PdfRect> quads, uint colorArgb, string? note = null, string? author = null)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return new(Guid.NewGuid(), PdfAnnotationKind.Highlight, quads.ToArray(), colorArgb, note, author, now, now);
    }

    public static PdfAnnotation NewComment(PdfRect iconRect, string? text, string? author = null)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return new(Guid.NewGuid(), PdfAnnotationKind.Comment, [iconRect], 0xFFFFD54F, text, author, now, now);
    }

    public static PdfAnnotation NewInk(
        IReadOnlyList<IReadOnlyList<PdfPoint>> strokes, uint colorArgb, double strokeWidth, string? author = null)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var copied = strokes.Select(s => (IReadOnlyList<PdfPoint>)s.ToArray()).ToArray();
        return new(Guid.NewGuid(), PdfAnnotationKind.Ink, [], colorArgb, null, author, now, now)
        {
            Strokes = copied,
            StrokeWidth = strokeWidth,
        };
    }
}

/// <summary>Payload for <see cref="PdfDocument.AnnotationsChanged"/>.</summary>
public sealed class AnnotationsChangedEventArgs(int pageIndex) : EventArgs
{
    /// <summary>The affected page, or <c>-1</c> when several/all pages changed (undo, redo, structural edit).</summary>
    public int PageIndex { get; } = pageIndex;
}
