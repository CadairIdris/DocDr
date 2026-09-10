using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DocDr.TestSupport;

/// <summary>
/// Generates tiny but valid multi-page PDFs with a real (non-embedded, base-14) text layer so
/// tests exercising rendering and text search do not need binary fixtures checked into the repo.
/// </summary>
public static class TestPdfBuilder
{
    public const double PageWidth = 612;
    public const double PageHeight = 792;

    public sealed record PdfInfo(string? Title = null, string? Author = null, string? Subject = null);

    /// <summary>A flat outline entry: a title that jumps to a 0-based page.</summary>
    public sealed record Bookmark(string Title, int PageIndex);

    /// <summary>
    /// Write a PDF whose page <c>i</c> contains the line <paramref name="pageLines"/>[i] drawn in
    /// 24pt Helvetica near the top-left.
    /// </summary>
    /// <summary>A <c>/Link</c> annotation: a rectangle near the top of <paramref name="FromPage"/>
    /// that jumps to <paramref name="ToPage"/> (both 0-based).</summary>
    public sealed record Link(int FromPage, int ToPage);

    public static string WritePdf(
        string path,
        IReadOnlyList<string> pageLines,
        PdfInfo? info = null,
        IReadOnlyList<Bookmark>? bookmarks = null,
        string? watermark = null,
        int watermarkOnFirstNPages = int.MaxValue,
        IReadOnlyList<Link>? links = null,
        double cropInset = 0,
        int romanFrontPages = 0)
    {
        byte[] bytes = Build(pageLines, info, bookmarks ?? [], watermark, watermarkOnFirstNPages, links ?? [], cropInset, romanFrontPages);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Build(
        IReadOnlyList<string> pageLines, PdfInfo? info, IReadOnlyList<Bookmark> bookmarks,
        string? watermark, int watermarkOnFirstNPages, IReadOnlyList<Link> links, double cropInset,
        int romanFrontPages)
    {
        int pageCount = pageLines.Count;
        var buffer = new MemoryStream();
        var offsets = new List<long> { 0 }; // object 0 is the free head

        void Write(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));
        void BeginObject(int number)
        {
            offsets.Add(buffer.Position);
            Write($"{number} 0 obj\n");
        }

        int PageObj(int index) => 4 + (2 * index);

        // Object-number plan (written strictly in this order).
        int next = 4 + (2 * pageCount);
        int? infoObj = info is not null ? next++ : null;
        int? outlinesObj = bookmarks.Count > 0 ? next++ : null;
        int firstItemObj = next;
        int linkFirstObj = next + bookmarks.Count;
        int totalObjects = linkFirstObj + links.Count - 1;

        // page index -> the link-annot object numbers on that page
        var annotsByPage = new Dictionary<int, List<int>>();
        for (int k = 0; k < links.Count; k++)
        {
            if (!annotsByPage.TryGetValue(links[k].FromPage, out List<int>? list))
            {
                annotsByPage[links[k].FromPage] = list = [];
            }

            list.Add(linkFirstObj + k);
        }

        Write("%PDF-1.7\n");
        buffer.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]); // binary marker comment

        string pageLabels = romanFrontPages > 0
            ? $" /PageLabels << /Nums [ 0 << /S /r >> {romanFrontPages} << /S /D >> ] >>"
            : string.Empty;
        BeginObject(1);
        Write(outlinesObj is null
            ? $"<< /Type /Catalog /Pages 2 0 R{pageLabels} >>\nendobj\n"
            : $"<< /Type /Catalog /Pages 2 0 R /Outlines {outlinesObj} 0 R{pageLabels} >>\nendobj\n");

        BeginObject(2);
        var kids = new StringBuilder();
        for (int i = 0; i < pageCount; i++)
        {
            kids.Append(CultureInfo.InvariantCulture, $"{PageObj(i)} 0 R ");
        }

        Write($"<< /Type /Pages /Kids [ {kids.ToString().TrimEnd()} ] /Count {pageCount} >>\nendobj\n");

        BeginObject(3);
        Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        for (int i = 0; i < pageCount; i++)
        {
            BeginObject(PageObj(i));
            string annots = annotsByPage.TryGetValue(i, out List<int>? pageAnnots)
                ? $" /Annots [ {string.Join(" ", pageAnnots.Select(a => $"{a} 0 R"))} ]"
                : string.Empty;
            string cropBox = cropInset > 0
                ? $" /CropBox [{F(cropInset)} {F(cropInset)} {F(PageWidth - cropInset)} {F(PageHeight - cropInset)}]"
                : string.Empty;
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(PageWidth)} {F(PageHeight)}]{cropBox} " +
                  $"/Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (2 * i)} 0 R{annots} >>\nendobj\n");

            string stream = $"BT /F1 24 Tf 72 {F(PageHeight - 96)} Td ({Escape(pageLines[i])}) Tj ET\n";
            if (watermark is not null && i < watermarkOnFirstNPages)
            {
                stream += $"BT /F1 8 Tf 40 40 Td ({Escape(watermark)}) Tj ET\n";
            }

            BeginObject(5 + (2 * i));
            Write($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream\nendobj\n");
        }

        if (infoObj is not null)
        {
            BeginObject(infoObj.Value);
            var dict = new StringBuilder("<< ");
            if (info!.Title is not null) dict.Append(CultureInfo.InvariantCulture, $"/Title ({Escape(info.Title)}) ");
            if (info.Author is not null) dict.Append(CultureInfo.InvariantCulture, $"/Author ({Escape(info.Author)}) ");
            if (info.Subject is not null) dict.Append(CultureInfo.InvariantCulture, $"/Subject ({Escape(info.Subject)}) ");
            dict.Append(">>");
            Write($"{dict}\nendobj\n");
        }

        if (outlinesObj is not null)
        {
            BeginObject(outlinesObj.Value);
            Write($"<< /Type /Outlines /First {firstItemObj} 0 R " +
                  $"/Last {firstItemObj + bookmarks.Count - 1} 0 R /Count {bookmarks.Count} >>\nendobj\n");

            for (int i = 0; i < bookmarks.Count; i++)
            {
                int self = firstItemObj + i;
                var dict = new StringBuilder();
                dict.Append(CultureInfo.InvariantCulture,
                    $"<< /Title ({Escape(bookmarks[i].Title)}) /Parent {outlinesObj} 0 R ");
                dict.Append(CultureInfo.InvariantCulture,
                    $"/Dest [ {PageObj(bookmarks[i].PageIndex)} 0 R /Fit ] ");
                if (i > 0) dict.Append(CultureInfo.InvariantCulture, $"/Prev {self - 1} 0 R ");
                if (i < bookmarks.Count - 1) dict.Append(CultureInfo.InvariantCulture, $"/Next {self + 1} 0 R ");
                dict.Append(">>");

                BeginObject(self);
                Write($"{dict}\nendobj\n");
            }
        }

        for (int k = 0; k < links.Count; k++)
        {
            // A wide band near the top of the source page.
            double y1 = PageHeight - 60;
            double y0 = y1 - 16;
            BeginObject(linkFirstObj + k);
            Write($"<< /Type /Annot /Subtype /Link /Rect [ 60 {F(y0)} {F(PageWidth - 60)} {F(y1)} ] " +
                  $"/Border [0 0 0] /Dest [ {PageObj(links[k].ToPage)} 0 R /Fit ] >>\nendobj\n");
        }

        long xrefPos = buffer.Position;
        Write($"xref\n0 {totalObjects + 1}\n");
        Write("0000000000 65535 f \n");
        for (int i = 1; i <= totalObjects; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {totalObjects + 1} /Root 1 0 R");
        if (infoObj is not null)
        {
            Write($" /Info {infoObj.Value} 0 R");
        }

        Write($" >>\nstartxref\n{xrefPos}\n%%EOF");

        return buffer.ToArray();
    }

    /// <summary>A run of text placed at an absolute page position (points, bottom-left origin).</summary>
    public sealed record Run(string Text, double X, double Y, double Size = 9);

    /// <summary>A filled rectangle (points, bottom-left origin) — table tests use thin ones as
    /// ruling lines, matching how real code PDFs draw their grids.</summary>
    public sealed record Rect(double X0, double Y0, double X1, double Y1);

    /// <summary>Write a one-page PDF with each run drawn at its exact position — for table tests.</summary>
    public static string WriteRuns(string path, IReadOnlyList<Run> runs) => WriteRuns(path, runs, []);

    /// <summary>Write a one-page PDF with text runs plus filled rectangles (table ruling lines).</summary>
    public static string WriteRuns(string path, IReadOnlyList<Run> runs, IReadOnlyList<Rect> rects)
    {
        var buffer = new MemoryStream();
        var offsets = new List<long> { 0 };
        void Write(string s) => buffer.Write(Encoding.ASCII.GetBytes(s));
        void BeginObject(int n)
        {
            offsets.Add(buffer.Position);
            Write($"{n} 0 obj\n");
        }

        Write("%PDF-1.7\n");
        buffer.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);

        BeginObject(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObject(2);
        Write("<< /Type /Pages /Kids [ 4 0 R ] /Count 1 >>\nendobj\n");
        BeginObject(3);
        Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        BeginObject(4);
        Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(PageWidth)} {F(PageHeight)}] " +
              "/Resources << /Font << /F1 3 0 R >> >> /Contents 5 0 R >>\nendobj\n");

        var stream = new StringBuilder();
        foreach (Rect box in rects)
        {
            stream.Append(CultureInfo.InvariantCulture,
                $"{F(box.X0)} {F(box.Y0)} {F(box.X1 - box.X0)} {F(box.Y1 - box.Y0)} re f\n");
        }

        foreach (Run run in runs)
        {
            stream.Append(CultureInfo.InvariantCulture,
                $"BT /F1 {F(run.Size)} Tf {F(run.X)} {F(run.Y)} Td ({Escape(run.Text)}) Tj ET\n");
        }

        BeginObject(5);
        Write($"<< /Length {Encoding.ASCII.GetByteCount(stream.ToString())} >>\nstream\n{stream}endstream\nendobj\n");

        long xrefPos = buffer.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF");
        File.WriteAllBytes(path, buffer.ToArray());
        return path;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
