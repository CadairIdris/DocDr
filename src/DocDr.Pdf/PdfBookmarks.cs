using System.Runtime.InteropServices;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>A node in a PDF's outline (bookmark) tree.</summary>
public sealed class PdfBookmark
{
    public PdfBookmark(string title, int? pageIndex, IReadOnlyList<PdfBookmark> children)
    {
        Title = title;
        PageIndex = pageIndex;
        Children = children;
    }

    public string Title { get; }

    /// <summary>Target page (0-based), or null if the bookmark has no resolvable go-to destination.</summary>
    public int? PageIndex { get; }

    public IReadOnlyList<PdfBookmark> Children { get; }
}

/// <summary>Reads the document outline via PDFium. Read-only; all work runs under the document lock.</summary>
public static class PdfBookmarks
{
    private const int MaxNodes = 8000;
    private const int MaxDepth = 32;

    public static IReadOnlyList<PdfBookmark> Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document.Locked(() =>
        {
            int budget = MaxNodes;
            FpdfBookmarkT? first = fpdf_doc.FPDFBookmarkGetFirstChild(document.Handle, null);
            return ReadSiblings(document.Handle, first, ref budget, 0);
        });
    }

    private static IReadOnlyList<PdfBookmark> ReadSiblings(FpdfDocumentT doc, FpdfBookmarkT? node, ref int budget, int depth)
    {
        if (depth >= MaxDepth)
        {
            return [];
        }

        var result = new List<PdfBookmark>();
        var seen = new HashSet<IntPtr>();

        while (node is not null && node.__Instance != IntPtr.Zero && budget-- > 0)
        {
            if (!seen.Add(node.__Instance))
            {
                break; // malformed: sibling chain loops back on itself
            }

            string title = ReadTitle(node);
            int? pageIndex = ResolvePageIndex(doc, node);
            IReadOnlyList<PdfBookmark> children = ReadSiblings(
                doc, fpdf_doc.FPDFBookmarkGetFirstChild(doc, node), ref budget, depth + 1);

            result.Add(new PdfBookmark(title, pageIndex, children));
            node = fpdf_doc.FPDFBookmarkGetNextSibling(doc, node);
        }

        return result;
    }

    private static string ReadTitle(FpdfBookmarkT node)
    {
        uint byteLength = fpdf_doc.FPDFBookmarkGetTitle(node, IntPtr.Zero, 0);
        if (byteLength <= 2)
        {
            return string.Empty;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)byteLength);
        try
        {
            fpdf_doc.FPDFBookmarkGetTitle(node, buffer, byteLength);
            return Marshal.PtrToStringUni(buffer)?.TrimEnd('\0') ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int? ResolvePageIndex(FpdfDocumentT doc, FpdfBookmarkT node)
    {
        FpdfDestT? dest = fpdf_doc.FPDFBookmarkGetDest(doc, node);

        if (dest is null || dest.__Instance == IntPtr.Zero)
        {
            FpdfActionT? action = fpdf_doc.FPDFBookmarkGetAction(node);
            if (action is not null && action.__Instance != IntPtr.Zero)
            {
                dest = fpdf_doc.FPDFActionGetDest(doc, action);
            }
        }

        if (dest is null || dest.__Instance == IntPtr.Zero)
        {
            return null;
        }

        int index = fpdf_doc.FPDFDestGetDestPageIndex(doc, dest);
        return index >= 0 ? index : null;
    }
}
