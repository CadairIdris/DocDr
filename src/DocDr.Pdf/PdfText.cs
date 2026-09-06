using System.Runtime.InteropServices;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>A single character and its bounding box in PDFium page space. Kept for Stage 3 selection work.</summary>
public readonly record struct PdfCharBox(int Index, string Text, PdfRect Box);

/// <summary>
/// Plain-text and character-geometry extraction for a page. All calls run under the document lock.
/// </summary>
public static class PdfTextExtractor
{
    /// <summary>Extracted text of one page, in reading order as PDFium reports it.</summary>
    public static string GetPageText(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidatePageIndex(pageIndex);

        return document.Locked(() => WithTextPage(document, pageIndex, textPage =>
        {
            int count = fpdf_text.FPDFTextCountChars(textPage);
            return count <= 0 ? string.Empty : ReadText(textPage, 0, count);
        }));
    }

    /// <summary>Per-character boxes for one page.</summary>
    public static IReadOnlyList<PdfCharBox> GetCharBoxes(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidatePageIndex(pageIndex);

        return document.Locked(() => WithTextPage(document, pageIndex, textPage =>
        {
            int count = fpdf_text.FPDFTextCountChars(textPage);
            var boxes = new List<PdfCharBox>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                double left = 0, right = 0, bottom = 0, top = 0;
                fpdf_text.FPDFTextGetCharBox(textPage, i, ref left, ref right, ref bottom, ref top);
                uint unicode = fpdf_text.FPDFTextGetUnicode(textPage, i);
                boxes.Add(new PdfCharBox(i, CodePointToString(unicode),
                    new PdfRect(left, top, right, bottom)));
            }

            return (IReadOnlyList<PdfCharBox>)boxes;
        }));
    }

    internal static T WithTextPage<T>(PdfDocument document, int pageIndex, Func<FpdfTextpageT, T> work)
    {
        FpdfPageT? page = fpdfview.FPDF_LoadPage(document.Handle, pageIndex);
        if (page is null || page.__Instance == IntPtr.Zero)
        {
            throw new PdfException(PdfiumError.PageNotFoundOrContentError, $"Could not load page {pageIndex}.");
        }

        try
        {
            FpdfTextpageT? textPage = fpdf_text.FPDFTextLoadPage(page);
            if (textPage is null || textPage.__Instance == IntPtr.Zero)
            {
                throw new PdfException(PdfiumError.PageNotFoundOrContentError, $"Could not load text for page {pageIndex}.");
            }

            try
            {
                return work(textPage);
            }
            finally
            {
                fpdf_text.FPDFTextClosePage(textPage);
            }
        }
        finally
        {
            fpdfview.FPDF_ClosePage(page);
        }
    }

    private static string CodePointToString(uint codePoint)
    {
        if (codePoint == 0 || codePoint > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF))
        {
            return string.Empty;
        }

        return char.ConvertFromUtf32((int)codePoint);
    }

    internal static string ReadText(FpdfTextpageT textPage, int start, int count)
    {
        // FPDFText_GetText writes count+1 UTF-16 units (incl. NUL terminator).
        var buffer = new ushort[count + 1];
        int written = fpdf_text.FPDFTextGetText(textPage, start, count, ref buffer[0]);
        int chars = Math.Max(0, Math.Min(written - 1, count));
        return chars <= 0 ? string.Empty : Utf16(buffer, chars);
    }

    internal static unsafe string Utf16(ushort[] units, int length)
    {
        fixed (ushort* p = units)
        {
            return new string((char*)p, 0, length);
        }
    }

    /// <summary>UTF-16, NUL-terminated, as PDFium's search / text APIs expect for <c>FPDF_WIDESTRING</c>.</summary>
    internal static ushort[] ToWideString(string value)
    {
        var units = new ushort[value.Length + 1];
        for (int i = 0; i < value.Length; i++)
        {
            units[i] = value[i];
        }

        units[value.Length] = 0;
        return units;
    }
}
