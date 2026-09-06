using System.Globalization;
using System.Text;

namespace DocDr.Pdf.Tests;

/// <summary>
/// Generates tiny but valid multi-page PDFs with a real (non-embedded, base-14) text layer so
/// tests exercising rendering and text search do not need binary fixtures checked into the repo.
/// </summary>
internal static class TestPdfBuilder
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
    public static string WritePdf(
        string path,
        IReadOnlyList<string> pageLines,
        PdfInfo? info = null,
        IReadOnlyList<Bookmark>? bookmarks = null,
        string? watermark = null,
        int watermarkOnFirstNPages = int.MaxValue)
    {
        byte[] bytes = Build(pageLines, info, bookmarks ?? [], watermark, watermarkOnFirstNPages);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Build(
        IReadOnlyList<string> pageLines, PdfInfo? info, IReadOnlyList<Bookmark> bookmarks,
        string? watermark = null, int watermarkOnFirstNPages = int.MaxValue)
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
        int totalObjects = next + bookmarks.Count - 1;

        Write("%PDF-1.7\n");
        buffer.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]); // binary marker comment

        BeginObject(1);
        Write(outlinesObj is null
            ? "<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"
            : $"<< /Type /Catalog /Pages 2 0 R /Outlines {outlinesObj} 0 R >>\nendobj\n");

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
            Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(PageWidth)} {F(PageHeight)}] " +
                  $"/Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (2 * i)} 0 R >>\nendobj\n");

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

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
