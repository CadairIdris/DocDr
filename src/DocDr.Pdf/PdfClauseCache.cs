using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocDr.Pdf;

/// <summary>
/// Caches a <see cref="PdfCodeStructure"/> — the Clauses nav panel's detected clause tree plus its
/// figure/table caption map — inside the PDF itself, as a private Flate-compressed stream object
/// referenced from the Catalog. <see cref="PdfClauses.ReadStructure"/> is a full page-by-page text
/// scan (~5s on a 400-page standard); a document that's already been scanned once shouldn't have to
/// pay for it again on every later open.
/// <para>
/// Same incremental-update technique as <see cref="PdfOutlineWriter"/> / <see cref="PdfMetadataWriter"/>
/// — PDFium has no setter for a new Catalog entry either. An unparseable or missing cache is treated
/// as "not scanned yet" rather than an error; a chain of several incremental updates (metadata,
/// outline, this) composes fine since each preserves whatever Catalog keys it doesn't understand.
/// </para>
/// </summary>
internal static partial class PdfClauseCache
{
    /// <summary>Byte form of the Catalog key, for a cheap whole-file pre-check before parsing:
    /// the overwhelming majority of opens are files DocDr has never cached, and this is one linear
    /// scan versus decoding the whole file into a string just to find nothing.</summary>
    private static readonly byte[] MarkerBytes = "/DocDrClauses"u8.ToArray();

    public static byte[] Append(byte[] pdf, PdfCodeStructure structure)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(structure);
        if (structure.Clauses.Count == 0)
        {
            return pdf;
        }

        string text = Encoding.Latin1.GetString(pdf);
        string tail = text[Math.Max(0, text.Length - 8192)..];

        Match startXref = LastMatch(StartXrefRegex(), tail);
        Match trailer = LastMatch(TrailerRegex(), tail);
        if (!startXref.Success || !trailer.Success)
        {
            return pdf;
        }

        Match root = RootRegex().Match(trailer.Groups[1].Value);
        Match size = SizeRegex().Match(trailer.Groups[1].Value);
        if (!root.Success || !size.Success)
        {
            return pdf;
        }

        int rootObj = int.Parse(root.Groups[1].Value, CultureInfo.InvariantCulture);
        int cacheObj = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
        long prevXref = long.Parse(startXref.Groups[1].Value, CultureInfo.InvariantCulture);

        if (FindObjectBody(text, rootObj) is not { } catalogBody)
        {
            return pdf;
        }

        byte[] compressed = Compress(structure);

        using var stream = new MemoryStream(pdf.Length + compressed.Length + 512);
        stream.Write(pdf);
        if (pdf.Length == 0 || pdf[^1] != (byte)'\n')
        {
            stream.WriteByte((byte)'\n');
        }

        var offsets = new List<(int Obj, long Offset)>();

        offsets.Add((cacheObj, stream.Length));
        WriteAscii(stream,
            $"{cacheObj} 0 obj\n<< /Type /DocDrClauseCache /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
        stream.Write(compressed);
        WriteAscii(stream, "\nendstream\nendobj\n");

        string newCatalog = CacheKeyRegex().IsMatch(catalogBody)
            ? CacheKeyRegex().Replace(catalogBody, $" /DocDrClauses {cacheObj} 0 R")
            : $"{catalogBody.TrimEnd()} /DocDrClauses {cacheObj} 0 R";
        offsets.Add((rootObj, stream.Length));
        WriteAscii(stream, $"{rootObj} 0 obj\n<< {newCatalog} >>\nendobj\n");

        long xrefOffset = stream.Length;
        var sb = new StringBuilder("xref\n");
        foreach ((int obj, long offset) in offsets.OrderBy(o => o.Obj))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{obj} 1\n{offset:D10} 00000 n \n");
        }

        int newSize = Math.Max(cacheObj + 1, rootObj + 1);
        sb.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {newSize} /Root {rootObj} 0 R /Prev {prevXref}");
        Match info = InfoRegex().Match(trailer.Groups[1].Value);
        if (info.Success)
        {
            sb.Append(CultureInfo.InvariantCulture, $" /Info {info.Groups[1].Value} 0 R");
        }

        sb.Append(CultureInfo.InvariantCulture, $" >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        WriteAscii(stream, sb.ToString());

        return stream.ToArray();
    }

    /// <summary>Read a previously-appended cache back, or null if there isn't one (never scanned,
    /// or the layout couldn't be parsed). Cheap in the common case — see <see cref="MarkerBytes"/>.</summary>
    public static PdfCodeStructure? TryRead(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (pdf.AsSpan().IndexOf(MarkerBytes) < 0)
        {
            return null;
        }

        try
        {
            return TryReadCore(pdf);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static PdfCodeStructure? TryReadCore(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);

        Match trailer = LastMatch(TrailerRegex(), text);
        if (!trailer.Success)
        {
            return null;
        }

        Match root = RootRegex().Match(trailer.Groups[1].Value);
        if (!root.Success)
        {
            return null;
        }

        int rootObj = int.Parse(root.Groups[1].Value, CultureInfo.InvariantCulture);
        if (FindObjectBody(text, rootObj) is not { } catalogBody)
        {
            return null;
        }

        Match cacheRef = CacheRefRegex().Match(catalogBody);
        if (!cacheRef.Success)
        {
            return null;
        }

        int cacheObj = int.Parse(cacheRef.Groups[1].Value, CultureInfo.InvariantCulture);
        if (FindObjectBody(text, cacheObj) is not { } cacheDict)
        {
            return null;
        }

        Match length = LengthRegex().Match(cacheDict);
        if (!length.Success)
        {
            return null;
        }

        int len = int.Parse(length.Groups[1].Value, CultureInfo.InvariantCulture);

        // FindObjectBody already located the *last* definition of cacheObj — reuse the same
        // "last occurrence" search to find where its dictionary (and stream) actually start.
        var objRegex = new Regex($@"(?<![0-9]){cacheObj}\s+\d+\s+obj\b", RegexOptions.CultureInvariant);
        Match objMatch = LastMatch(objRegex, text);
        int dictEnd = text.IndexOf(">>", objMatch.Index + objMatch.Length, StringComparison.Ordinal);
        int streamKeyword = dictEnd >= 0 ? text.IndexOf("stream", dictEnd, StringComparison.Ordinal) : -1;
        if (streamKeyword < 0)
        {
            return null;
        }

        int dataStart = streamKeyword + "stream".Length;
        if (dataStart < text.Length && text[dataStart] == '\r')
        {
            dataStart++;
        }

        if (dataStart < text.Length && text[dataStart] == '\n')
        {
            dataStart++;
        }

        if (dataStart < 0 || dataStart + len > pdf.Length)
        {
            return null;
        }

        return Decompress(pdf[dataStart..(dataStart + len)]);
    }

    private static byte[] Compress(PdfCodeStructure structure)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(structure);
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(json);
        }

        return output.ToArray();
    }

    private static PdfCodeStructure? Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return JsonSerializer.Deserialize<PdfCodeStructure>(output.ToArray());
    }

    /// <summary>The dictionary body (between the outer <c>&lt;&lt;</c> and matching <c>&gt;&gt;</c>) of
    /// the most recent definition of <paramref name="objNum"/>, or null.</summary>
    private static string? FindObjectBody(string pdf, int objNum)
    {
        var re = new Regex($@"(?<![0-9]){objNum}\s+\d+\s+obj\b", RegexOptions.CultureInvariant);
        Match last = LastMatch(re, pdf);
        if (!last.Success)
        {
            return null;
        }

        int i = pdf.IndexOf("<<", last.Index + last.Length, StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        int depth = 0;
        for (int p = i; p < pdf.Length - 1; p++)
        {
            if (pdf[p] == '<' && pdf[p + 1] == '<')
            {
                depth++;
                p++;
            }
            else if (pdf[p] == '>' && pdf[p + 1] == '>')
            {
                depth--;
                p++;
                if (depth == 0)
                {
                    return pdf[(i + 2)..(p - 1)];
                }
            }
        }

        return null;
    }

    private static void WriteAscii(Stream stream, string s) => stream.Write(Encoding.Latin1.GetBytes(s));

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

    [GeneratedRegex(@"/Root\s+(\d+)\s+\d+\s+R")]
    private static partial Regex RootRegex();

    [GeneratedRegex(@"/Size\s+(\d+)")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"/Info\s+(\d+)\s+\d+\s+R")]
    private static partial Regex InfoRegex();

    [GeneratedRegex(@"/Length\s+(\d+)")]
    private static partial Regex LengthRegex();

    [GeneratedRegex(@"\s*/DocDrClauses\s+\d+\s+\d+\s+R")]
    private static partial Regex CacheKeyRegex();

    [GeneratedRegex(@"/DocDrClauses\s+(\d+)\s+\d+\s+R")]
    private static partial Regex CacheRefRegex();
}
