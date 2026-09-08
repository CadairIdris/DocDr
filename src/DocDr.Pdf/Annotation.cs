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

    /// <summary>A bordered text box placed on the page (authored as a <c>Stamp</c> appearance).</summary>
    TextBox,

    /// <summary>A text box with a leader line + arrow pointing at a spot (a <c>Stamp</c> appearance).</summary>
    Callout,

    /// <summary>A scalloped "revision cloud" outlining a changed area (a <c>Stamp</c> appearance).</summary>
    Cloud,

    /// <summary>A pasted raster image placed on the page (a <c>Stamp</c> appearance).</summary>
    Image,

    /// <summary>A plain rectangle outline (a <c>Stamp</c> appearance).</summary>
    Rectangle,

    /// <summary>A plain ellipse outline (a <c>Stamp</c> appearance).</summary>
    Ellipse,

    /// <summary>A straight line between two points (a <c>Stamp</c> appearance).</summary>
    Line,

    /// <summary>A straight line with an arrowhead at its end point (a <c>Stamp</c> appearance).</summary>
    Arrow,
}

/// <summary>
/// One message in a comment thread: the reply text plus who wrote it and when. The thread's
/// opening message is the <see cref="PdfAnnotation"/> itself (its <c>Contents</c> / author /
/// dates); <see cref="PdfAnnotation.Replies"/> holds the rest in order.
/// </summary>
public sealed record PdfReply(
    Guid Id,
    string Text,
    string? Author,
    DateTimeOffset Created,
    DateTimeOffset Modified)
{
    public static PdfReply New(string text, string? author)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return new PdfReply(Guid.NewGuid(), text, author, now, now);
    }
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

    /// <summary>Text size in points for <see cref="PdfAnnotationKind.TextBox"/> / <see cref="PdfAnnotationKind.Callout"/>.</summary>
    public double FontSize { get; init; }

    /// <summary>Whether a text box / callout box still auto-fits its text (cleared once the user resizes it).</summary>
    public bool AutoSize { get; init; } = true;

    /// <summary>Outline width in points for the stroked stamp kinds (rectangle, ellipse, line, arrow,
    /// callout, cloud, text-box border). <c>0</c> means "use the default" (<see cref="DefaultLineWidth"/>).</summary>
    public double LineWidth { get; init; }

    /// <summary>Fill colour (ARGB) for a <see cref="PdfAnnotationKind.Rectangle"/> / <see cref="PdfAnnotationKind.Ellipse"/>;
    /// <c>null</c> for no fill.</summary>
    public uint? FillArgb { get; init; }

    /// <summary>Whether the outline is drawn dashed (rectangle, ellipse, line, arrow).</summary>
    public bool Dashed { get; init; }

    /// <summary>Text box / callout: suppress the box outline (a borderless label).</summary>
    public bool Borderless { get; init; }

    /// <summary>Text colour (ARGB) for a text box / callout; <c>null</c> means black.</summary>
    public uint? TextColorArgb { get; init; }

    /// <summary>Replies to this comment, oldest first. Empty for a thread with no replies yet.</summary>
    public IReadOnlyList<PdfReply> Replies { get; init; } = [];

    /// <summary>Encoded (PNG) bytes for a <see cref="PdfAnnotationKind.Image"/>; null otherwise.</summary>
    public byte[]? ImageData { get; init; }

    /// <summary>Stamp-backed kinds whose appearance DocDr builds itself.</summary>
    public bool IsStampBacked => IsStampKind(Kind);

    /// <summary>Whether <paramref name="kind"/> is drawn as a DocDr-built <c>Stamp</c> appearance.</summary>
    public static bool IsStampKind(PdfAnnotationKind kind) => kind is PdfAnnotationKind.TextBox
        or PdfAnnotationKind.Callout or PdfAnnotationKind.Cloud or PdfAnnotationKind.Image
        or PdfAnnotationKind.Rectangle or PdfAnnotationKind.Ellipse or PdfAnnotationKind.Line
        or PdfAnnotationKind.Arrow;

    /// <summary>PDF annotation subtype number for <see cref="Kind"/> (matches the PDF spec / PDFium).</summary>
    public int Subtype => Kind switch
    {
        PdfAnnotationKind.Highlight => 9,
        PdfAnnotationKind.Ink => 15,
        _ when IsStampBacked => 13, // Stamp — DocDr builds the appearance
        _ => 1,
    };

    /// <summary>The box rectangle for a box-shaped kind (text box, callout, cloud, image, rectangle,
    /// ellipse) — the first quad. Default for the others.</summary>
    public PdfRect Box => Quads.Count > 0 ? Quads[0] : default;

    /// <summary>The two/more control points for a <see cref="PdfAnnotationKind.Callout"/> leader, or
    /// the two end points of a <see cref="PdfAnnotationKind.Line"/> / <see cref="PdfAnnotationKind.Arrow"/>
    /// (tip / arrowhead first).</summary>
    public IReadOnlyList<PdfPoint> Leader =>
        Kind is PdfAnnotationKind.Callout or PdfAnnotationKind.Line or PdfAnnotationKind.Arrow
        && Strokes.Count > 0 ? Strokes[0] : [];

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

            double pad = Kind switch
            {
                PdfAnnotationKind.Ink => Math.Max(1, StrokeWidth),
                PdfAnnotationKind.Cloud => 6,   // the scallops bulge outside the vertex rectangle
                PdfAnnotationKind.Arrow => 6,   // the arrowhead pokes past the end point
                _ => 0,
            };
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

    /// <summary>The default text size for a new text box / callout, in points.</summary>
    public const double DefaultFontSize = 11;

    /// <summary>The default outline width, in points, when <see cref="LineWidth"/> is <c>0</c>.</summary>
    public const double DefaultLineWidth = 1.5;

    /// <summary>The outline width to actually draw with (never <c>0</c>).</summary>
    public double EffectiveLineWidth => LineWidth > 0 ? LineWidth : DefaultLineWidth;

    public static PdfAnnotation NewTextBox(
        PdfRect box, string? text, uint colorArgb, double fontSize = DefaultFontSize, string? author = null)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return new(Guid.NewGuid(), PdfAnnotationKind.TextBox, [box], colorArgb, text, author, now, now)
        {
            FontSize = fontSize > 0 ? fontSize : DefaultFontSize,
        };
    }

    public static PdfAnnotation NewCallout(
        PdfRect box, IReadOnlyList<PdfPoint> leader, string? text, uint colorArgb,
        double fontSize = DefaultFontSize, string? author = null)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return new(Guid.NewGuid(), PdfAnnotationKind.Callout, [box], colorArgb, text, author, now, now)
        {
            Strokes = [leader.ToArray()],
            FontSize = fontSize > 0 ? fontSize : DefaultFontSize,
        };
    }

    public static PdfAnnotation NewCloud(PdfRect area, uint colorArgb, string? author = null)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return new(Guid.NewGuid(), PdfAnnotationKind.Cloud, [area], colorArgb, null, author, now, now);
    }

    public static PdfAnnotation NewImage(PdfRect box, byte[] png, string? author = null)
    {
        ArgumentNullException.ThrowIfNull(png);
        DateTimeOffset now = DateTimeOffset.Now;
        return new(Guid.NewGuid(), PdfAnnotationKind.Image, [box], 0, null, author, now, now)
        {
            ImageData = png,
            AutoSize = false,
        };
    }

    public static PdfAnnotation NewRectangle(PdfRect box, uint colorArgb, string? author = null) =>
        NewBoxShape(PdfAnnotationKind.Rectangle, box, colorArgb, author);

    public static PdfAnnotation NewEllipse(PdfRect box, uint colorArgb, string? author = null) =>
        NewBoxShape(PdfAnnotationKind.Ellipse, box, colorArgb, author);

    private static PdfAnnotation NewBoxShape(PdfAnnotationKind kind, PdfRect box, uint colorArgb, string? author)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        return new(Guid.NewGuid(), kind, [box], colorArgb, null, author, now, now) { AutoSize = false };
    }

    public static PdfAnnotation NewLine(PdfPoint from, PdfPoint to, uint colorArgb, string? author = null) =>
        NewLineShape(PdfAnnotationKind.Line, from, to, colorArgb, author);

    public static PdfAnnotation NewArrow(PdfPoint from, PdfPoint to, uint colorArgb, string? author = null) =>
        NewLineShape(PdfAnnotationKind.Arrow, from, to, colorArgb, author);

    private static PdfAnnotation NewLineShape(PdfAnnotationKind kind, PdfPoint from, PdfPoint to, uint colorArgb, string? author)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        // Strokes[0] = [end, start] so index 0 is the arrowhead tip (matches the callout leader order).
        return new(Guid.NewGuid(), kind, [], colorArgb, null, author, now, now)
        {
            Strokes = [[to, from]],
        };
    }
}

/// <summary>Payload for <see cref="PdfDocument.AnnotationsChanged"/>.</summary>
public sealed class AnnotationsChangedEventArgs(int pageIndex) : EventArgs
{
    /// <summary>The affected page, or <c>-1</c> when several/all pages changed (undo, redo, structural edit).</summary>
    public int PageIndex { get; } = pageIndex;
}
