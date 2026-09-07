using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocDr.Pdf;

/// <summary>
/// Serialises the geometry of a stamp-backed annotation (text box, callout, revision cloud) to
/// and from the JSON string DocDr keeps in the annotation's private <c>/DocDrShape</c> key.
/// <para>
/// These are written to the PDF as <c>Stamp</c> annotations with a DocDr-built appearance stream
/// (PDFium has no setter for <c>/CL</c> / <c>/Vertices</c> / <c>/BE</c>), so the shape itself only
/// survives a round trip through DocDr via this key — other readers still see the appearance.
/// </para>
/// </summary>
internal static class PdfShapeCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Shape(string Kind, double[] Box, double[][]? Leader, double FontSize);

    public static string Encode(PdfAnnotation a)
    {
        double[][]? leader = a.Leader.Count > 0
            ? a.Leader.Select(p => new[] { p.X, p.Y }).ToArray()
            : null;

        var shape = new Shape(
            a.Kind.ToString(),
            [a.Box.Left, a.Box.Top, a.Box.Right, a.Box.Bottom],
            leader,
            a.FontSize);

        return JsonSerializer.Serialize(shape, Options);
    }

    /// <summary>Rebuild the stamp-backed annotation from its <c>/DocDrShape</c> string plus the
    /// standard fields already read off the annotation. Returns <c>null</c> if the JSON is unusable.</summary>
    public static PdfAnnotation? Decode(
        string? json, Guid id, uint colorArgb, string? contents, string? author,
        DateTimeOffset? created, DateTimeOffset? modified, IReadOnlyList<PdfReply> replies)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        Shape? shape;
        try
        {
            shape = JsonSerializer.Deserialize<Shape>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (shape is null || shape.Box is not { Length: 4 }
            || !Enum.TryParse(shape.Kind, out PdfAnnotationKind kind)
            || kind is not (PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout or PdfAnnotationKind.Cloud))
        {
            return null;
        }

        var box = new PdfRect(shape.Box[0], shape.Box[1], shape.Box[2], shape.Box[3]);
        IReadOnlyList<IReadOnlyList<PdfPoint>> strokes = shape.Leader is { Length: > 0 }
            ? [shape.Leader.Where(p => p.Length == 2).Select(p => new PdfPoint(p[0], p[1])).ToArray()]
            : [];

        return new PdfAnnotation(id, kind, [box], colorArgb, contents, author, created, modified)
        {
            Strokes = strokes,
            FontSize = shape.FontSize > 0 ? shape.FontSize : PdfAnnotation.DefaultFontSize,
            Replies = replies,
        };
    }
}
