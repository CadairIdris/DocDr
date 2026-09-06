namespace DocDr.Pdf;

/// <summary>The annotation kinds DocDr authors and round-trips.</summary>
public enum PdfAnnotationKind
{
    /// <summary>A text-markup highlight over one or more runs of text (PDF subtype <c>Highlight</c>).</summary>
    Highlight,

    /// <summary>A sticky-note comment anchored to a point (PDF subtype <c>Text</c>).</summary>
    Comment,
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
    DateTimeOffset? Modified)
{
    /// <summary>PDF annotation subtype number for <see cref="Kind"/> (matches the PDF spec / PDFium).</summary>
    public int Subtype => Kind == PdfAnnotationKind.Highlight ? 9 : 1;

    /// <summary>Smallest rectangle covering every quad.</summary>
    public PdfRect Bounds
    {
        get
        {
            if (Quads.Count == 0)
            {
                return default;
            }

            double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
            foreach (PdfRect q in Quads)
            {
                left = Math.Min(left, Math.Min(q.Left, q.Right));
                right = Math.Max(right, Math.Max(q.Left, q.Right));
                bottom = Math.Min(bottom, Math.Min(q.Top, q.Bottom));
                top = Math.Max(top, Math.Max(q.Top, q.Bottom));
            }

            return new PdfRect(left, top, right, bottom);
        }
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(Contents);

    public static PdfAnnotation NewHighlight(IReadOnlyList<PdfRect> quads, uint colorArgb, string? note = null, string? author = null) =>
        new(Guid.NewGuid(), PdfAnnotationKind.Highlight, quads.ToArray(), colorArgb, note, author, DateTimeOffset.Now);

    public static PdfAnnotation NewComment(PdfRect iconRect, string? text, string? author = null) =>
        new(Guid.NewGuid(), PdfAnnotationKind.Comment, [iconRect], 0xFFFFD54F, text, author, DateTimeOffset.Now);
}

/// <summary>Payload for <see cref="PdfDocument.AnnotationsChanged"/>.</summary>
public sealed class AnnotationsChangedEventArgs(int pageIndex) : EventArgs
{
    /// <summary>The affected page, or <c>-1</c> when several/all pages changed (undo, redo, structural edit).</summary>
    public int PageIndex { get; } = pageIndex;
}
