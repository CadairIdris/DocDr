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
            bool ourStamp = subtype == PdfAnnotations.SubtypeStamp
                && fpdf_annot.FPDFAnnotHasKey(annot, PdfAnnotations.ShapeKey) != 0;
            fpdf_annot.FPDFPageCloseAnnot(annot);

            if (subtype is PdfAnnotations.SubtypeHighlight or PdfAnnotations.SubtypeText or PdfAnnotations.SubtypeInk
                || ourStamp)
            {
                fpdf_annot.FPDFPageRemoveAnnot(page, i);
            }
        }
    }

    public static void Write(
        FpdfDocumentT doc, FpdfPageT page, IReadOnlyList<PdfAnnotation> annotations, IImageDecoder? imageDecoder = null)
    {
        bool needsFont = annotations.Any(a => a.Kind is PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout);
        FpdfFontT? font = needsFont ? fpdf_edit.FPDFTextLoadStandardFont(doc, "Helvetica") : null;

        try
        {
            foreach (PdfAnnotation a in annotations)
            {
                WriteOne(doc, page, font, a, imageDecoder);
            }
        }
        finally
        {
            if (font is not null && font.__Instance != IntPtr.Zero)
            {
                fpdf_edit.FPDFFontClose(font);
            }
        }
    }

    private static void WriteOne(
        FpdfDocumentT doc, FpdfPageT page, FpdfFontT? font, PdfAnnotation a, IImageDecoder? imageDecoder)
    {
        FpdfAnnotationT? annot = fpdf_annot.FPDFPageCreateAnnot(page, a.Subtype);
        if (annot is null || annot.__Instance == IntPtr.Zero)
        {
            return;
        }

        try
        {
            SetColor(annot, a.ColorArgb, a.Kind == PdfAnnotationKind.Ink ? (uint)150 : 255);
            SetRect(annot, a.Bounds);

            if (a.Kind == PdfAnnotationKind.Highlight)
            {
                foreach (PdfRect quad in a.Quads)
                {
                    AppendQuad(annot, quad);
                }
            }

            if (a.Kind == PdfAnnotationKind.Ink)
            {
                fpdf_annot.FPDFAnnotSetBorder(annot, 0, 0, (float)Math.Max(1, a.StrokeWidth));
                foreach (IReadOnlyList<PdfPoint> stroke in a.Strokes)
                {
                    PdfInkInterop.AddStroke(annot, stroke);
                }
            }

            if (a.IsStampBacked)
            {
                PdfStampAppearance.Build(doc, annot, font, a, imageDecoder);
                SetString(annot, PdfAnnotations.ShapeKey, PdfShapeCodec.Encode(a));
                if (a.Kind == PdfAnnotationKind.Image && a.ImageData is { Length: > 0 } png)
                {
                    SetString(annot, PdfAnnotations.ImageKey, Convert.ToBase64String(png));
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

            // The reply thread rides along on the parent as one private-key string —
            // PDFium can't write a standard /IRT reply-annotation chain (no ref setter).
            if (a.Replies.Count > 0)
            {
                SetString(annot, "DocDrThread", PdfReplyCodec.Encode(a.Replies));
            }
        }
        finally
        {
            fpdf_annot.FPDFPageCloseAnnot(annot);
        }
    }

    private static void SetColor(FpdfAnnotationT annot, uint argb, uint alpha)
    {
        uint r = (argb >> 16) & 0xFF;
        uint g = (argb >> 8) & 0xFF;
        uint b = argb & 0xFF;
        fpdf_annot.FPDFAnnotSetColor(annot, FPDFANNOT_COLORTYPE.FPDFANNOT_COLORTYPE_Color, r, g, b, alpha);
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
