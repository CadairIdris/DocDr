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

    /// <summary>Per-character boxes plus each glyph's rotation (radians, ≥ 0; -1 if PDFium can't
    /// say). Table extraction uses the angle to ignore the rotated "uncontrolled copy" strip that
    /// runs up the page margin, which a loose table selection would otherwise pull in.</summary>
    internal static IReadOnlyList<(PdfCharBox Box, double Angle)> GetCharBoxesWithAngle(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.ValidatePageIndex(pageIndex);

        return document.Locked(() => WithTextPage(document, pageIndex, textPage =>
        {
            int count = fpdf_text.FPDFTextCountChars(textPage);
            var boxes = new List<(PdfCharBox, double)>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                double left = 0, right = 0, bottom = 0, top = 0;
                fpdf_text.FPDFTextGetCharBox(textPage, i, ref left, ref right, ref bottom, ref top);
                uint unicode = fpdf_text.FPDFTextGetUnicode(textPage, i);
                double angle = fpdf_text.FPDFTextGetCharAngle(textPage, i);
                boxes.Add((new PdfCharBox(i, CodePointToString(unicode), new PdfRect(left, top, right, bottom)), angle));
            }

            return (IReadOnlyList<(PdfCharBox, double)>)boxes;
        }));
    }

    /// <summary>Padding added around each merged line box, as a fraction of that line's height —
    /// scales with font size rather than a fixed point value looking right on one size and wrong
    /// on another. Keeps the highlight from sitting flush against the glyph edges (ascenders,
    /// descenders, serifs), which read as slightly clipped otherwise.</summary>
    private const double LinePaddingFraction = 0.12;

    /// <summary>
    /// Merge a run of glyph boxes into one rectangle per text line, in the order given. Chars
    /// whose vertical span still overlaps the current run's are the same line/word and widen one
    /// box; a genuine line break doesn't overlap, so it starts a fresh one. Degenerate boxes
    /// (spaces, zero-size glyphs) are skipped without breaking the run — otherwise every word
    /// would land in its own rectangle wherever a space split the run. Used for both the
    /// click-drag text selection highlight and search-hit highlights, so a search match reads as
    /// one clean box around the word rather than a per-character (or PDFium-per-run) staircase.
    /// Each finished box gets a small pad (see <see cref="LinePaddingFraction"/>) on all four
    /// sides, applied after merging so it never affects the same-line decision above.
    /// </summary>
    public static IReadOnlyList<PdfRect> MergeIntoLineRects(IEnumerable<PdfRect> boxes)
    {
        var quads = new List<PdfRect>();
        double left = 0, right = 0, top = 0, bottom = 0;
        bool inRun = false;

        void Flush()
        {
            if (inRun && right > left && top > bottom)
            {
                double pad = (top - bottom) * LinePaddingFraction;
                quads.Add(new PdfRect(left - pad, top + pad, right + pad, bottom - pad));
            }

            inRun = false;
        }

        foreach (PdfRect b in boxes)
        {
            if (b.Right - b.Left < 0.5 || b.Top - b.Bottom < 0.5)
            {
                continue;
            }

            if (inRun)
            {
                double overlap = Math.Min(top, b.Top) - Math.Max(bottom, b.Bottom);
                double glyphHeight = Math.Max(1, b.Top - b.Bottom);
                if (overlap < glyphHeight * 0.35)
                {
                    Flush();
                }
            }

            if (!inRun)
            {
                left = b.Left;
                right = b.Right;
                top = b.Top;
                bottom = b.Bottom;
                inRun = true;
            }
            else
            {
                left = Math.Min(left, b.Left);
                right = Math.Max(right, b.Right);
                top = Math.Max(top, b.Top);
                bottom = Math.Min(bottom, b.Bottom);
            }
        }

        Flush();
        return quads;
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
