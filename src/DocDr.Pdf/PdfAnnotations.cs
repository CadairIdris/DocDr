using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>
/// Reads DocDr-managed annotations (highlights and text notes) from a page via PDFium. Other
/// annotation subtypes are ignored here and left untouched in the file. All work runs under the
/// document lock. Mirrors <see cref="PdfBookmarks"/>.
/// </summary>
public static class PdfAnnotations
{
    internal const int SubtypeText = 1;
    internal const int SubtypeHighlight = 9;
    internal const int SubtypeStamp = 13;
    internal const int SubtypeInk = 15;

    /// <summary>Private annotation key carrying a stamp-backed shape's real geometry (JSON).</summary>
    internal const string ShapeKey = "DocDrShape";

    public static IReadOnlyList<PdfAnnotation> Read(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidatePageIndex(pageIndex);
        return document.Locked(() => ReadLocked(document.Handle, pageIndex));
    }

    /// <summary>Caller already holds the document lock and the page belongs to <paramref name="handle"/>.</summary>
    internal static IReadOnlyList<PdfAnnotation> ReadLocked(FpdfDocumentT handle, int pageIndex)
    {
        FpdfPageT? page = fpdfview.FPDF_LoadPage(handle, pageIndex);
        if (page is null || page.__Instance == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            int count = fpdf_annot.FPDFPageGetAnnotCount(page);
            var result = new List<PdfAnnotation>();

            for (int i = 0; i < count; i++)
            {
                FpdfAnnotationT? annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                if (annot is null || annot.__Instance == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    int subtype = fpdf_annot.FPDFAnnotGetSubtype(annot);
                    bool ourStamp = subtype == SubtypeStamp && fpdf_annot.FPDFAnnotHasKey(annot, ShapeKey) != 0;
                    if (subtype is not (SubtypeHighlight or SubtypeText or SubtypeInk) && !ourStamp)
                    {
                        continue;
                    }

                    string? contents = ReadString(annot, "Contents");
                    string? author = ReadString(annot, "T");
                    DateTimeOffset? created = PdfDate.TryParse(ReadString(annot, "CreationDate"), out DateTimeOffset c) ? c : null;
                    DateTimeOffset? modified = PdfDate.TryParse(ReadString(annot, "M"), out DateTimeOffset m) ? m : null;
                    Guid id = Guid.TryParse(ReadString(annot, "NM"), out Guid nm) ? nm : Guid.NewGuid();
                    IReadOnlyList<PdfReply> replies = PdfReplyCodec.Decode(ReadString(annot, "DocDrThread"));

                    if (ourStamp)
                    {
                        PdfAnnotation? shape = PdfShapeCodec.Decode(
                            ReadString(annot, ShapeKey), id, ReadColor(annot), contents, author, created, modified, replies);
                        if (shape is not null)
                        {
                            result.Add(shape);
                        }

                        continue;
                    }

                    if (subtype == SubtypeInk)
                    {
                        IReadOnlyList<IReadOnlyList<PdfPoint>> strokes = PdfInkInterop.ReadStrokes(annot);
                        if (strokes.Count == 0)
                        {
                            continue;
                        }

                        result.Add(new PdfAnnotation(id, PdfAnnotationKind.Ink, [], ReadColor(annot),
                            contents, author, created, modified)
                        {
                            Strokes = strokes,
                            StrokeWidth = ReadBorderWidth(annot),
                        });
                        continue;
                    }

                    PdfAnnotationKind kind = subtype == SubtypeHighlight
                        ? PdfAnnotationKind.Highlight
                        : PdfAnnotationKind.Comment;
                    IReadOnlyList<PdfRect> quads = subtype == SubtypeHighlight
                        ? ReadQuads(annot)
                        : [ReadRect(annot)];

                    result.Add(new PdfAnnotation(id, kind, quads, ReadColor(annot), contents, author, created, modified)
                    {
                        Replies = replies,
                    });
                }
                finally
                {
                    fpdf_annot.FPDFPageCloseAnnot(annot);
                }
            }

            return result;
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    private static IReadOnlyList<PdfRect> ReadQuads(FpdfAnnotationT annot)
    {
        ulong quadCount = fpdf_annot.FPDFAnnotCountAttachmentPoints(annot);
        if (quadCount == 0)
        {
            return [ReadRect(annot)];
        }

        var quads = new List<PdfRect>((int)quadCount);
        for (ulong i = 0; i < quadCount; i++)
        {
            using var q = new FS_QUADPOINTSF();
            if (fpdf_annot.FPDFAnnotGetAttachmentPoints(annot, i, q) == 0)
            {
                continue;
            }

            double left = Math.Min(Math.Min(q.X1, q.X2), Math.Min(q.X3, q.X4));
            double right = Math.Max(Math.Max(q.X1, q.X2), Math.Max(q.X3, q.X4));
            double bottom = Math.Min(Math.Min(q.Y1, q.Y2), Math.Min(q.Y3, q.Y4));
            double top = Math.Max(Math.Max(q.Y1, q.Y2), Math.Max(q.Y3, q.Y4));
            quads.Add(new PdfRect(left, top, right, bottom));
        }

        return quads.Count > 0 ? quads : [ReadRect(annot)];
    }

    private static PdfRect ReadRect(FpdfAnnotationT annot)
    {
        using var r = new FS_RECTF_();
        if (fpdf_annot.FPDFAnnotGetRect(annot, r) == 0)
        {
            return default;
        }

        return new PdfRect(r.Left, Math.Max(r.Top, r.Bottom), r.Right, Math.Min(r.Top, r.Bottom));
    }

    private static double ReadBorderWidth(FpdfAnnotationT annot)
    {
        float h = 0, v = 0, w = 0;
        return fpdf_annot.FPDFAnnotGetBorder(annot, ref h, ref v, ref w) != 0 && w > 0 ? w : 8.0;
    }

    private static uint ReadColor(FpdfAnnotationT annot)
    {
        uint red = 0, green = 0, blue = 0, alpha = 0;
        if (fpdf_annot.FPDFAnnotGetColor(annot, FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color,
                ref red, ref green, ref blue, ref alpha) == 0)
        {
            return 0xFFFFD54F;
        }

        return (0xFFu << 24) | ((red & 0xFF) << 16) | ((green & 0xFF) << 8) | (blue & 0xFF);
    }

    private static string? ReadString(FpdfAnnotationT annot, string key)
    {
        ushort probe = 0;
        uint byteLength = fpdf_annot.FPDFAnnotGetStringValue(annot, key, ref probe, 0);
        if (byteLength <= 2)
        {
            return null;
        }

        var buffer = new ushort[(byteLength / 2) + 1];
        fpdf_annot.FPDFAnnotGetStringValue(annot, key, ref buffer[0], byteLength);
        int chars = Math.Max(0, (int)(byteLength / 2) - 1); // trailing UTF-16 NUL
        string text = PdfTextExtractor.Utf16(buffer, chars).TrimEnd('\0');
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
