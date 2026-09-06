using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>
/// Writes DocDr-managed annotations onto a live PDFium page just before a save, and strips them
/// back off afterwards so the in-memory handle stays overlay-only. Caller holds the document lock
/// and owns the <see cref="FpdfPageT"/>.
/// </summary>
internal static class PdfAnnotationWriter
{
    /// <summary>Remove every highlight / text-note annotation from the page (highest index first).</summary>
    public static void StripManaged(FpdfPageT page)
    {
        for (int i = fpdf_annot.FPDFPageGetAnnotCount(page) - 1; i >= 0; i--)
        {
            FpdfAnnotationT? annot = fpdf_annot.FPDFPageGetAnnot(page, i);
            if (annot is null || annot.__Instance == IntPtr.Zero)
            {
                continue;
            }

            int subtype = fpdf_annot.FPDFAnnotGetSubtype(annot);
            fpdf_annot.FPDFPageCloseAnnot(annot);

            if (subtype is PdfAnnotations.SubtypeHighlight or PdfAnnotations.SubtypeText)
            {
                fpdf_annot.FPDFPageRemoveAnnot(page, i);
            }
        }
    }

    public static void Write(FpdfPageT page, IReadOnlyList<PdfAnnotation> annotations)
    {
        foreach (PdfAnnotation a in annotations)
        {
            FpdfAnnotationT? annot = fpdf_annot.FPDFPageCreateAnnot(page, a.Subtype);
            if (annot is null || annot.__Instance == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                SetColor(annot, a.ColorArgb);
                SetRect(annot, a.Bounds);

                if (a.Kind == PdfAnnotationKind.Highlight)
                {
                    foreach (PdfRect quad in a.Quads)
                    {
                        AppendQuad(annot, quad);
                    }
                }

                if (!string.IsNullOrEmpty(a.Contents))
                {
                    SetString(annot, "Contents", a.Contents);
                }

                if (!string.IsNullOrWhiteSpace(a.Author))
                {
                    SetString(annot, "T", a.Author);
                }

                DateTimeOffset now = DateTimeOffset.Now;
                SetString(annot, "CreationDate", PdfDate.Format(a.Created ?? a.Modified ?? now));
                SetString(annot, "M", PdfDate.Format(a.Modified ?? now));

                // A stable id lets other readers thread replies / recognise the annotation.
                SetString(annot, "NM", a.Id.ToString());
            }
            finally
            {
                fpdf_annot.FPDFPageCloseAnnot(annot);
            }
        }
    }

    private static void SetColor(FpdfAnnotationT annot, uint argb)
    {
        uint r = (argb >> 16) & 0xFF;
        uint g = (argb >> 8) & 0xFF;
        uint b = argb & 0xFF;
        fpdf_annot.FPDFAnnotSetColor(annot, FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color, r, g, b, 255);
    }

    private static void SetRect(FpdfAnnotationT annot, PdfRect rect)
    {
        using var r = new FS_RECTF_
        {
            Left = (float)rect.Left,
            Right = (float)rect.Right,
            Top = (float)Math.Max(rect.Top, rect.Bottom),
            Bottom = (float)Math.Min(rect.Top, rect.Bottom),
        };
        fpdf_annot.FPDFAnnotSetRect(annot, r);
    }

    private static void AppendQuad(FpdfAnnotationT annot, PdfRect rect)
    {
        double top = Math.Max(rect.Top, rect.Bottom);
        double bottom = Math.Min(rect.Top, rect.Bottom);
        using var q = new FS_QUADPOINTSF
        {
            X1 = (float)rect.Left, Y1 = (float)top,
            X2 = (float)rect.Right, Y2 = (float)top,
            X3 = (float)rect.Left, Y3 = (float)bottom,
            X4 = (float)rect.Right, Y4 = (float)bottom,
        };
        fpdf_annot.FPDFAnnotAppendAttachmentPoints(annot, q);
    }

    private static void SetString(FpdfAnnotationT annot, string key, string value)
    {
        ushort[] wide = PdfTextExtractor.ToWideString(value);
        fpdf_annot.FPDFAnnotSetStringValue(annot, key, ref wide[0]);
    }
}
