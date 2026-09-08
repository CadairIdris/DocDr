using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocDr.Pdf;

/// <summary>
/// Writes Info-dictionary metadata by appending an incremental update (PDF 1.7 §7.5.6) to an
/// already-serialised PDF. PDFium has no metadata setter and always emits a classic
/// cross-reference table with a <c>trailer</c> keyword, which this relies on.
/// </summary>
internal static partial class PdfMetadataWriter
{
    private static readonly string[] Fields =
        ["Title", "Author", "Subject", "Keywords", "Creator", "Producer", "CreationDate", "ModDate"];

    public static byte[] AppendInfo(byte[] pdf, PdfDocumentInfo info)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        string tail = Encoding.Latin1.GetString(pdf, Math.Max(0, pdf.Length - 8192), Math.Min(8192, pdf.Length));

        Match startXref = LastMatch(StartXrefRegex(), tail);
        Match trailer = LastMatch(TrailerRegex(), tail);
        if (!startXref.Success || !trailer.Success)
        {
            // Unexpected layout (e.g. xref stream) — leave the file as PDFium wrote it.
            return pdf;
        }

        string trailerBody = trailer.Groups[1].Value;
        Match root = RootRegex().Match(trailerBody);
        Match size = SizeRegex().Match(trailerBody);
        if (!root.Success || !size.Success)
        {
            return pdf;
        }

        long prevXref = long.Parse(startXref.Groups[1].Value, CultureInfo.InvariantCulture);
        int infoObj = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);

        using var stream = new MemoryStream(pdf.Length + 512);
        stream.Write(pdf);
        if (pdf.Length == 0 || pdf[^1] != (byte)'\n')
        {
            stream.WriteByte((byte)'\n');
        }

        long objOffset = stream.Length;
        WriteAscii(stream, $"{infoObj} 0 obj\n{BuildInfoDictionary(info)}\nendobj\n");

        long xrefOffset = stream.Length;
        WriteAscii(stream,
            $"xref\n{infoObj} 1\n{objOffset:D10} 00000 n \n" +
            $"trailer\n<< /Size {infoObj + 1} /Root {root.Groups[1].Value} {root.Groups[2].Value} R " +
            $"/Prev {prevXref} /Info {infoObj} 0 R >>\n" +
            $"startxref\n{xrefOffset}\n%%EOF\n");

        return stream.ToArray();
    }

    private static string BuildInfoDictionary(PdfDocumentInfo info)
    {
        var sb = new StringBuilder("<<");
        foreach (string field in Fields)
        {
            string value = field switch
            {
                "Title" => info.Title,
                "Author" => info.Author,
                "Subject" => info.Subject,
                "Keywords" => info.Keywords,
                "Creator" => info.Creator,
                "Producer" => info.Producer,
                "CreationDate" => info.CreationDate,
                "ModDate" => info.ModificationDate,
                _ => string.Empty,
            };

            if (!string.IsNullOrEmpty(value))
            {
                sb.Append(CultureInfo.InvariantCulture, $" /{field} {PdfString(value)}");
            }
        }

        sb.Append(" >>");
        return sb.ToString();
    }

    /// <summary>An ASCII-safe PDF string token: a literal <c>(...)</c> for printable ASCII, else a UTF-16BE hex string.</summary>
    internal static string PdfString(string value)
    {
        bool asciiPrintable = value.All(c => c is >= ' ' and <= '~');
        if (asciiPrintable)
        {
            string escaped = value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            return $"({escaped})";
        }

        var hex = new StringBuilder("<FEFF");
        foreach (char c in value)
        {
            hex.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }

        hex.Append('>');
        return hex.ToString();
    }

    private static void WriteAscii(Stream stream, string text)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        stream.Write(bytes);
    }

    private static Match LastMatch(Regex regex, string input)
    {
        Match last = Match.Empty;
        for (Match m = regex.Match(input); m.Success; m = m.NextMatch())
        {
            last = m;
        }

        return last;
    }

    [GeneratedRegex(@"startxref\s+(\d+)")]
    private static partial Regex StartXrefRegex();

    [GeneratedRegex(@"trailer\s*<<(.*?)>>\s*startxref", RegexOptions.Singleline)]
    private static partial Regex TrailerRegex();

    [GeneratedRegex(@"/Root\s+(\d+)\s+(\d+)\s+R")]
    private static partial Regex RootRegex();

    [GeneratedRegex(@"/Size\s+(\d+)")]
    private static partial Regex SizeRegex();
}
