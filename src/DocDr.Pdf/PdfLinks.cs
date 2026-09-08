using System.Runtime.InteropServices;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>
/// A clickable link region on a page — a table-of-contents entry, a cross-reference, or an
/// external URL. Coordinates are in <b>unrotated</b> page space (same as
/// <see cref="PdfAnnotations"/> quads and <c>FPDFText_GetRect</c>).
/// </summary>
public sealed record PdfLink(PdfRect Rect, int? TargetPageIndex, string? Uri);

/// <summary>
/// Reads the <c>/Link</c> annotations on a page via PDFium and resolves each one to a target
/// page (GoTo actions / destinations) or a URL (URI actions). Read-only; all work runs under
/// the document lock. Mirrors <see cref="PdfBookmarks"/> / <see cref="PdfAnnotations"/>.
/// </summary>
public static class PdfLinks
{
    private const int SubtypeLink = 2; // FPDF_ANNOT_LINK

    private const uint ActionGoto = 1;  // PDFACTION_GOTO
    private const uint ActionUri = 3;   // PDFACTION_URI

    public static IReadOnlyList<PdfLink> Read(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidatePageIndex(pageIndex);
        return document.Locked(() =>
        {
            IReadOnlyList<PdfLink> annotationLinks = ReadLocked(document.Handle, pageIndex);
            IReadOnlyList<PdfLink> webLinks;
            try
            {
                webLinks = ReadWebLinksLocked(document, pageIndex);
            }
            catch (PdfException)
            {
                webLinks = []; // no text layer on this page
            }

            if (webLinks.Count == 0)
            {
                return annotationLinks;
            }

            // Keep every annotation link; add only web links that don't sit on top of one.
            var merged = new List<PdfLink>(annotationLinks);
            foreach (PdfLink web in webLinks)
            {
                if (!annotationLinks.Any(a => Overlaps(a.Rect, web.Rect)))
                {
                    merged.Add(web);
                }
            }

            return (IReadOnlyList<PdfLink>)merged;
        });
    }

    private static bool Overlaps(PdfRect a, PdfRect b)
    {
        double ax0 = Math.Min(a.Left, a.Right), ax1 = Math.Max(a.Left, a.Right);
        double ay0 = Math.Min(a.Top, a.Bottom), ay1 = Math.Max(a.Top, a.Bottom);
        double bx0 = Math.Min(b.Left, b.Right), bx1 = Math.Max(b.Left, b.Right);
        double by0 = Math.Min(b.Top, b.Bottom), by1 = Math.Max(b.Top, b.Bottom);
        return ax0 < bx1 && bx0 < ax1 && ay0 < by1 && by0 < ay1;
    }

    /// <summary>
    /// Bare URLs / emails printed in the page text with no <c>/Link</c> annotation, found by
    /// PDFium's web-link detector (<c>FPDFLink_*WebLinks</c>). One <see cref="PdfLink"/> per
    /// on-page rectangle (a link that wraps a line has several).
    /// </summary>
    internal static IReadOnlyList<PdfLink> ReadWebLinksLocked(PdfDocument document, int pageIndex)
    {
        return PdfTextExtractor.WithTextPage(document, pageIndex, textPage =>
        {
            FpdfPagelinkT? pageLink = fpdf_text.FPDFLinkLoadWebLinks(textPage);
            if (pageLink is null || pageLink.__Instance == IntPtr.Zero)
            {
                return (IReadOnlyList<PdfLink>)[];
            }

            try
            {
                var result = new List<PdfLink>();
                int count = fpdf_text.FPDFLinkCountWebLinks(pageLink);
                for (int i = 0; i < count; i++)
                {
                    string? uri = ReadWebLinkUri(pageLink, i);
                    if (uri is null)
                    {
                        continue;
                    }

                    int rects = fpdf_text.FPDFLinkCountRects(pageLink, i);
                    for (int r = 0; r < rects; r++)
                    {
                        double left = 0, top = 0, right = 0, bottom = 0;
                        fpdf_text.FPDFLinkGetRect(pageLink, i, r, ref left, ref top, ref right, ref bottom);
                        var box = new PdfRect(left, Math.Max(top, bottom), right, Math.Min(top, bottom));
                        if (box.Width >= 1 && box.Height >= 1)
                        {
                            result.Add(new PdfLink(box, null, uri));
                        }
                    }
                }

                return (IReadOnlyList<PdfLink>)result;
            }
            finally
            {
                fpdf_text.FPDFLinkCloseWebLinks(pageLink);
            }
        });
    }

    private static string? ReadWebLinkUri(FpdfPagelinkT pageLink, int index)
    {
        ushort probe = 0;
        int units = fpdf_text.FPDFLinkGetURL(pageLink, index, ref probe, 0);
        if (units <= 1)
        {
            return null;
        }

        var buffer = new ushort[units];
        fpdf_text.FPDFLinkGetURL(pageLink, index, ref buffer[0], units);
        string uri = PdfTextExtractor.Utf16(buffer, units - 1).TrimEnd('\0').Trim();

        // PDFium already restricts these to http/https/mailto/ftp-style; be defensive anyway.
        return uri.Length >= 4 && (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("ftp", StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;
    }

    /// <summary>Caller already holds the document lock and the page belongs to <paramref name="handle"/>.</summary>
    internal static IReadOnlyList<PdfLink> ReadLocked(FpdfDocumentT handle, int pageIndex)
    {
        FpdfPageT? page = fpdfview.FPDF_LoadPage(handle, pageIndex);
        if (page is null || page.__Instance == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            int count = fpdf_annot.FPDFPageGetAnnotCount(page);
            var result = new List<PdfLink>();

            for (int i = 0; i < count; i++)
            {
                FpdfAnnotationT? annot = fpdf_annot.FPDFPageGetAnnot(page, i);
                if (annot is null || annot.__Instance == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (fpdf_annot.FPDFAnnotGetSubtype(annot) != SubtypeLink)
                    {
                        continue;
                    }

                    using var rect = new FS_RECTF_();
                    if (fpdf_annot.FPDFAnnotGetRect(annot, rect) == 0)
                    {
                        continue;
                    }

                    var bounds = new PdfRect(
                        rect.Left,
                        Math.Max(rect.Top, rect.Bottom),
                        rect.Right,
                        Math.Min(rect.Top, rect.Bottom));
                    if (bounds.Width < 1 || bounds.Height < 1)
                    {
                        continue;
                    }

                    FpdfLinkT? link = fpdf_annot.FPDFAnnotGetLink(annot);
                    if (link is null || link.__Instance == IntPtr.Zero)
                    {
                        continue;
                    }

                    (int? target, string? uri) = Resolve(handle, link);
                    if (target is null && uri is null)
                    {
                        continue;
                    }

                    result.Add(new PdfLink(bounds, target, uri));
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

    private static (int? Target, string? Uri) Resolve(FpdfDocumentT doc, FpdfLinkT link)
    {
        FpdfDestT? dest = fpdf_doc.FPDFLinkGetDest(doc, link);

        if (dest is null || dest.__Instance == IntPtr.Zero)
        {
            FpdfActionT? action = fpdf_doc.FPDFLinkGetAction(link);
            if (action is not null && action.__Instance != IntPtr.Zero)
            {
                uint type = fpdf_doc.FPDFActionGetType(action);
                if (type == ActionGoto)
                {
                    dest = fpdf_doc.FPDFActionGetDest(doc, action);
                }
                else if (type == ActionUri)
                {
                    return (null, ReadUri(doc, action));
                }
            }
        }

        if (dest is null || dest.__Instance == IntPtr.Zero)
        {
            return (null, null);
        }

        int index = fpdf_doc.FPDFDestGetDestPageIndex(doc, dest);
        return (index >= 0 ? index : null, null);
    }

    private static string? ReadUri(FpdfDocumentT doc, FpdfActionT action)
    {
        uint byteLength = fpdf_doc.FPDFActionGetURIPath(doc, action, IntPtr.Zero, 0);
        if (byteLength <= 1)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)byteLength);
        try
        {
            fpdf_doc.FPDFActionGetURIPath(doc, action, buffer, byteLength);
            // A PDF URI action path is 7-bit ASCII / PDFDocEncoding.
            string? uri = Marshal.PtrToStringAnsi(buffer)?.TrimEnd('\0');
            return string.IsNullOrWhiteSpace(uri) ? null : uri;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
