using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DocDr.Pdf;

/// <summary>
/// Removes repeating-content watermarks from serialised PDF bytes by editing the page
/// content streams directly, then appending an incremental update.
/// <para>
/// This exists because the obvious route — <c>FPDFPageRemoveObject</c> +
/// <c>FPDFPageGenerateContent</c> — makes PDFium re-serialise the <em>whole</em> page from its
/// object model, and that drops the <c>TJ</c> positioning arrays that kern tabular text, so
/// tables and headers come out scrambled. Working on the bytes lets us delete only the
/// watermark's own show operators (and drop content streams that hold nothing else), leaving
/// every other operator byte-for-byte intact.
/// </para>
/// <para>
/// The input is always PDFium's <c>FPDF_SaveAsCopy</c> output, which is a classic
/// <c>xref</c> table + <c>trailer</c> with plain (non-object-stream) page dictionaries — the
/// same assumption <see cref="PdfMetadataWriter"/> relies on. Anything unexpected returns the
/// input unchanged.
/// </para>
/// </summary>
internal static partial class PdfWatermarkStripper
{
    /// <summary>
    /// Strip the watermarks identified by <paramref name="textSignatures"/> (normalised text
    /// runs, see <see cref="PdfWatermarks.NormalizeText"/>) and <paramref name="imageHashes"/>
    /// (SHA-256 hex of the raw image XObject stream) from every page of <paramref name="pdf"/>.
    /// Returns the input unchanged if there is nothing to do or the file is not in the shape
    /// this expects.
    /// </summary>
    public static byte[] Strip(
        byte[] pdf,
        IReadOnlyCollection<string> textSignatures,
        IReadOnlyCollection<string> imageHashes)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(textSignatures);
        ArgumentNullException.ThrowIfNull(imageHashes);

        if (textSignatures.Count == 0 && imageHashes.Count == 0)
        {
            return pdf;
        }

        var textSigs = textSignatures as HashSet<string> ?? [.. textSignatures];
        string latin = Encoding.Latin1.GetString(pdf);

        Match trailer = TrailerRegex().Matches(latin).LastOrDefault() is { Success: true } t ? t : Match.Empty;
        MatchCollection startxrefs = StartXrefRegex().Matches(latin);
        if (!trailer.Success || startxrefs.Count == 0)
        {
            return pdf; // not a classic xref file — leave it alone
        }

        var dicts = EnumerateDicts(latin).ToList();
        var wmImageObjects = FindWatermarkImageObjects(latin, pdf, imageHashes);

        var contentCache = new Dictionary<int, (byte[] Data, bool Flate)?>();
        (byte[] Data, bool Flate)? GetContent(int objNum)
        {
            if (contentCache.TryGetValue(objNum, out (byte[] Data, bool Flate)? cached))
            {
                return cached;
            }

            (byte[] Data, bool Flate)? result = null;
            Match m = Regex.Match(latin, $@"(?<![0-9]){objNum}\s+0\s+obj\b");
            if (m.Success)
            {
                int streamPos = latin.IndexOf("stream", m.Index, StringComparison.Ordinal);
                int endObj = latin.IndexOf("endobj", m.Index, StringComparison.Ordinal);
                if (streamPos >= 0 && (endObj < 0 || streamPos < endObj))
                {
                    string dict = latin[m.Index..streamPos];
                    (int start, int end) = StreamBodyRange(latin, pdf, streamPos);
                    if (start >= 0)
                    {
                        bool flate = dict.Contains("/FlateDecode", StringComparison.Ordinal);
                        try
                        {
                            result = (flate ? Inflate(pdf[start..end]) : pdf[start..end], flate);
                        }
                        catch (InvalidDataException)
                        {
                            result = null;
                        }
                    }
                }
            }

            contentCache[objNum] = result;
            return result;
        }

        string DictOf(int objNum)
        {
            foreach ((int obj, string dict, _, _) in dicts)
            {
                if (obj == objNum)
                {
                    return dict;
                }
            }

            return string.Empty;
        }

        HashSet<string> WatermarkImageNames(string pageDict)
        {
            var names = new HashSet<string>();
            if (wmImageObjects.Count == 0)
            {
                return names;
            }

            string resources = Regex.Match(pageDict, @"/Resources\s*<<(.*?)>>", RegexOptions.Singleline) is { Success: true } ri
                ? ri.Groups[1].Value
                : Regex.Match(pageDict, @"/Resources\s+(\d+)\s+0\s+R") is { Success: true } rr
                    ? DictOf(int.Parse(rr.Groups[1].Value))
                    : string.Empty;
            if (resources.Length == 0)
            {
                return names;
            }

            string xobject = Regex.Match(resources, @"/XObject\s*<<(.*?)>>", RegexOptions.Singleline) is { Success: true } xi
                ? xi.Groups[1].Value
                : Regex.Match(resources, @"/XObject\s+(\d+)\s+0\s+R") is { Success: true } xr
                    ? DictOf(int.Parse(xr.Groups[1].Value))
                    : string.Empty;

            foreach (Match nm in NameRefRegex().Matches(xobject))
            {
                if (wmImageObjects.Contains(int.Parse(nm.Groups[2].Value)))
                {
                    names.Add(nm.Groups[1].Value);
                }
            }

            return names;
        }

        int nextObject = 1;
        foreach (Match m in ObjHeaderRegex().Matches(latin))
        {
            nextObject = Math.Max(nextObject, int.Parse(m.Groups[1].Value) + 1);
        }

        var appended = new SortedDictionary<int, byte[]>();
        bool anyChange = false;

        foreach ((int pageObj, string dict, _, _) in dicts)
        {
            if (!Regex.IsMatch(dict, @"/Type\s*/Page(?![A-Za-z])"))
            {
                continue;
            }

            (List<int> refs, bool inlineArray) = ContentRefs(dict, latin, GetContent);
            if (refs.Count == 0)
            {
                continue;
            }

            HashSet<string> wmImageNames = WatermarkImageNames(dict);
            var keep = new List<int>();
            bool pageChanged = false;

            foreach (int r in refs)
            {
                (byte[] Data, bool Flate)? content = GetContent(r);
                if (content is null)
                {
                    keep.Add(r);
                    continue;
                }

                (byte[] cleaned, bool changed, bool drop) = CleanStream(content.Value.Data, textSigs, wmImageNames);
                if (drop)
                {
                    pageChanged = true;
                    continue;
                }

                if (!changed)
                {
                    keep.Add(r);
                    continue;
                }

                pageChanged = true;
                int newObj = nextObject++;
                appended[newObj] = BuildStreamObject(newObj, cleaned, content.Value.Flate);
                keep.Add(newObj);
            }

            if (!pageChanged)
            {
                continue;
            }

            anyChange = true;
            string newContents = "/Contents [" + string.Join(" ", keep.Select(k => $"{k} 0 R")) + "]";
            string newDict = inlineArray
                ? Regex.Replace(dict, @"/Contents\s*\[[^\]]*\]", newContents)
                : Regex.Replace(dict, @"/Contents\s+\d+\s+0\s+R", newContents);
            appended[pageObj] = Encoding.Latin1.GetBytes($"{pageObj} 0 obj\n<<{newDict}>>\nendobj\n");
        }

        if (!anyChange)
        {
            return pdf;
        }

        return AppendIncrementalUpdate(pdf, latin, trailer, startxrefs[^1].Groups[1].Value, appended, nextObject);
    }

    private static (List<int> Refs, bool InlineArray) ContentRefs(
        string dict, string latin, Func<int, (byte[] Data, bool Flate)?> getContent)
    {
        var refs = new List<int>();

        Match arr = Regex.Match(dict, @"/Contents\s*\[([^\]]*)\]");
        if (arr.Success)
        {
            foreach (Match r in ObjRefRegex().Matches(arr.Groups[1].Value))
            {
                refs.Add(int.Parse(r.Groups[1].Value));
            }

            return (refs, true);
        }

        Match one = Regex.Match(dict, @"/Contents\s+(\d+)\s+0\s+R");
        if (!one.Success)
        {
            return (refs, false);
        }

        int single = int.Parse(one.Groups[1].Value);
        if (getContent(single) is not null)
        {
            refs.Add(single);
            return (refs, false);
        }

        // /Contents points at an indirect array object
        Match indirectArray = Regex.Match(latin, $@"(?<![0-9]){single}\s+0\s+obj\s*\[([^\]]*)\]");
        if (indirectArray.Success)
        {
            foreach (Match r in ObjRefRegex().Matches(indirectArray.Groups[1].Value))
            {
                refs.Add(int.Parse(r.Groups[1].Value));
            }
        }

        return (refs, false);
    }

    private static HashSet<int> FindWatermarkImageObjects(string latin, byte[] pdf, IReadOnlyCollection<string> imageHashes)
    {
        var result = new HashSet<int>();
        if (imageHashes.Count == 0)
        {
            return result;
        }

        var hashes = imageHashes as HashSet<string> ?? [.. imageHashes];
        foreach (Match m in ObjHeaderRegex().Matches(latin))
        {
            int streamPos = latin.IndexOf("stream", m.Index, StringComparison.Ordinal);
            int endObj = latin.IndexOf("endobj", m.Index, StringComparison.Ordinal);
            if (streamPos < 0 || (endObj >= 0 && streamPos > endObj))
            {
                continue;
            }

            if (!latin[m.Index..streamPos].Contains("/Image", StringComparison.Ordinal))
            {
                continue;
            }

            (int start, int end) = StreamBodyRange(latin, pdf, streamPos);
            if (start < 0)
            {
                continue;
            }

            if (hashes.Contains(Convert.ToHexString(SHA256.HashData(pdf[start..end]))))
            {
                result.Add(int.Parse(m.Groups[1].Value));
            }
        }

        return result;
    }

    private static byte[] AppendIncrementalUpdate(
        byte[] pdf,
        string latin,
        Match trailer,
        string previousStartXref,
        SortedDictionary<int, byte[]> appended,
        int nextObject)
    {
        string root = Regex.Match(trailer.Groups[1].Value, @"/Root\s+(\d+\s+\d+\s+R)").Groups[1].Value;
        int size = Math.Max(
            int.Parse(Regex.Match(trailer.Groups[1].Value, @"/Size\s+(\d+)").Groups[1].Value),
            nextObject);

        using var ms = new MemoryStream(pdf.Length + 4096);
        ms.Write(pdf, 0, pdf.Length);
        if (pdf[^1] != (byte)'\n')
        {
            ms.WriteByte((byte)'\n');
        }

        var offsets = new SortedDictionary<int, long>();
        foreach ((int obj, byte[] body) in appended)
        {
            offsets[obj] = ms.Position;
            ms.Write(body, 0, body.Length);
        }

        long xrefPos = ms.Position;
        var xref = new StringBuilder("xref\n");
        int? runStart = null;
        var run = new List<long>();

        void Flush()
        {
            if (runStart is int start)
            {
                xref.Append($"{start} {run.Count}\n");
                foreach (long offset in run)
                {
                    xref.Append($"{offset:D10} 00000 n \n");
                }
            }

            runStart = null;
            run.Clear();
        }

        int previous = int.MinValue;
        foreach ((int obj, long offset) in offsets)
        {
            if (obj != previous + 1)
            {
                Flush();
                runStart = obj;
            }

            run.Add(offset);
            previous = obj;
        }

        Flush();
        xref.Append($"trailer\n<< /Size {size} /Root {root} /Prev {previousStartXref} >>\nstartxref\n{xrefPos}\n%%EOF\n");
        ms.Write(Encoding.Latin1.GetBytes(xref.ToString()));
        return ms.ToArray();
    }

    private static byte[] BuildStreamObject(int objNum, byte[] content, bool flate)
    {
        byte[] body = flate ? Deflate(content) : content;
        string head = $"{objNum} 0 obj\n<< /Length {body.Length}{(flate ? " /Filter /FlateDecode" : string.Empty)} >>\nstream\n";
        const string tail = "\nendstream\nendobj\n";

        var full = new byte[head.Length + body.Length + tail.Length];
        Encoding.Latin1.GetBytes(head).CopyTo(full, 0);
        body.CopyTo(full, head.Length);
        Encoding.Latin1.GetBytes(tail).CopyTo(full, head.Length + body.Length);
        return full;
    }

    /// <summary>
    /// Remove every watermark show operator (and watermark <c>Do</c>) from one content stream.
    /// Returns <c>drop = true</c> when the stream held nothing but watermark content.
    /// </summary>
    private static (byte[] OutBytes, bool Changed, bool Drop) CleanStream(
        byte[] content, HashSet<string> textSignatures, HashSet<string> wmImageNames)
    {
        string c = Encoding.Latin1.GetString(content);
        List<(string Token, int Start, int End)> tokens = Tokenize(c);

        var operands = new List<(string Token, int Start, int End)>();
        var cuts = new List<(int Start, int End)>();
        bool hasPaint = false;
        bool keptShow = false;
        bool keptDo = false;
        bool cutAny = false;

        bool IsWatermarkText(string raw)
        {
            string normalized = PdfWatermarks.NormalizeText(raw);
            return normalized.Length >= 4 && textSignatures.Contains(normalized);
        }

        foreach ((string token, int start, int end) in tokens)
        {
            char first = token.Length > 0 ? token[0] : '\0';
            bool isOperand = first is '/' or '(' or '<' or '[' or ']' || char.IsDigit(first) || first is '-' or '+' or '.';
            if (isOperand)
            {
                operands.Add((token, start, end));
                continue;
            }

            int opStart = operands.Count > 0 ? operands[0].Start : start;
            switch (token)
            {
                case "Tj" or "'" or "\"":
                {
                    string text = DecodeString(operands.LastOrDefault(o => o.Token.StartsWith('(') || o.Token.StartsWith('<')).Token);
                    if (IsWatermarkText(text))
                    {
                        cuts.Add((opStart, end));
                        cutAny = true;
                    }
                    else
                    {
                        keptShow = true;
                    }

                    break;
                }

                case "TJ":
                {
                    var sb = new StringBuilder();
                    string array = operands.LastOrDefault(o => o.Token.StartsWith('[')).Token ?? string.Empty;
                    foreach (Match part in TjStringRegex().Matches(array))
                    {
                        sb.Append(DecodeString(part.Value));
                    }

                    if (IsWatermarkText(sb.ToString()))
                    {
                        cuts.Add((opStart, end));
                        cutAny = true;
                    }
                    else
                    {
                        keptShow = true;
                    }

                    break;
                }

                case "Do":
                {
                    string? name = operands.LastOrDefault(o => o.Token.StartsWith('/')).Token;
                    if (name is not null && wmImageNames.Contains(name))
                    {
                        cuts.Add((opStart, end));
                        cutAny = true;
                    }
                    else
                    {
                        keptDo = true;
                    }

                    break;
                }

                case "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "S" or "s" or "sh":
                    hasPaint = true;
                    break;
            }

            operands.Clear();
        }

        if (!cutAny)
        {
            return (content, false, false);
        }

        if (!hasPaint && !keptShow && !keptDo)
        {
            return (content, true, true);
        }

        cuts.Sort((a, b) => a.Start.CompareTo(b.Start));
        var outText = new StringBuilder(c.Length);
        int pos = 0;
        foreach ((int start, int end) in cuts)
        {
            if (start < pos)
            {
                continue;
            }

            outText.Append(c, pos, start - pos);
            pos = end;
        }

        outText.Append(c, pos, c.Length - pos);
        return (Encoding.Latin1.GetBytes(outText.ToString()), true, false);
    }

    // --- tiny PDF plumbing ---------------------------------------------------------------

    private static (int Start, int End) StreamBodyRange(string latin, byte[] pdf, int streamKeywordPos)
    {
        int start = streamKeywordPos + "stream".Length;
        if (start < pdf.Length && pdf[start] == 13)
        {
            start++;
        }

        if (start < pdf.Length && pdf[start] == 10)
        {
            start++;
        }

        int es = latin.IndexOf("endstream", start, StringComparison.Ordinal);
        if (es < 0)
        {
            return (-1, -1);
        }

        int end = es;
        while (end > start && (pdf[end - 1] == 10 || pdf[end - 1] == 13))
        {
            end--;
        }

        return (start, end);
    }

    private static IEnumerable<(int Obj, string Dict, int DictStart, int DictEnd)> EnumerateDicts(string s)
    {
        foreach (Match m in DictHeaderRegex().Matches(s))
        {
            int i = m.Index + m.Length;
            int depth = 1;
            int startInner = i;
            while (i < s.Length && depth > 0)
            {
                if (i + 1 < s.Length && s[i] == '<' && s[i + 1] == '<')
                {
                    depth++;
                    i += 2;
                    continue;
                }

                if (i + 1 < s.Length && s[i] == '>' && s[i + 1] == '>')
                {
                    depth--;
                    i += 2;
                    continue;
                }

                if (s[i] == '(')
                {
                    int d = 0;
                    while (i < s.Length)
                    {
                        if (s[i] == '\\')
                        {
                            i += 2;
                            continue;
                        }

                        if (s[i] == '(')
                        {
                            d++;
                        }
                        else if (s[i] == ')')
                        {
                            d--;
                            if (d == 0)
                            {
                                i++;
                                break;
                            }
                        }

                        i++;
                    }

                    continue;
                }

                i++;
            }

            int innerEnd = Math.Max(startInner, i - 2);
            yield return (int.Parse(m.Groups[1].Value), s[startInner..innerEnd], startInner, innerEnd);
        }
    }

    private static List<(string Token, int Start, int End)> Tokenize(string c)
    {
        var list = new List<(string, int, int)>();
        int i = 0;
        int n = c.Length;

        void Add(int start, int end)
        {
            end = Math.Min(end, n);
            if (end > start)
            {
                list.Add((c[start..end], start, end));
            }
        }

        while (i < n)
        {
            char ch = c[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            int start = i;
            if (ch == '%')
            {
                while (i < n && c[i] != '\n' && c[i] != '\r')
                {
                    i++;
                }

                continue;
            }

            if (ch == '(')
            {
                int depth = 0;
                while (i < n)
                {
                    if (c[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (c[i] == '(')
                    {
                        depth++;
                    }
                    else if (c[i] == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            break;
                        }
                    }

                    i++;
                }

                Add(start, i);
                continue;
            }

            if (ch == '<' && i + 1 < n && c[i + 1] == '<')
            {
                i += 2;
                Add(start, i);
                continue;
            }

            if (ch == '>' && i + 1 < n && c[i + 1] == '>')
            {
                i += 2;
                Add(start, i);
                continue;
            }

            if (ch == '<')
            {
                while (i < n && c[i] != '>')
                {
                    i++;
                }

                if (i < n)
                {
                    i++;
                }

                Add(start, i);
                continue;
            }

            if (ch == '[' || ch == ']')
            {
                i++;
                if (ch == '[')
                {
                    int d = 1;
                    while (i < n && d > 0)
                    {
                        if (c[i] == '\\')
                        {
                            i += 2;
                            continue;
                        }

                        if (c[i] == '[')
                        {
                            d++;
                        }
                        else if (c[i] == ']')
                        {
                            d--;
                        }

                        i++;
                    }
                }

                Add(start, i);
                continue;
            }

            if (ch == '/')
            {
                i++;
                while (i < n && !IsDelimiter(c[i]) && !char.IsWhiteSpace(c[i]))
                {
                    i++;
                }

                Add(start, i);
                continue;
            }

            while (i < n && !IsDelimiter(c[i]) && !char.IsWhiteSpace(c[i]))
            {
                i++;
            }

            if (i == start)
            {
                i++;
            }

            if (c[start..Math.Min(i, n)] == "BI")
            {
                // Inline image: BI <dict> ID <binary> EI. The binary is not tokens — skip it whole.
                int id = c.IndexOf("ID", i, StringComparison.Ordinal);
                if (id < 0)
                {
                    Add(start, i);
                    continue;
                }

                int p = id + 3; // ID + one whitespace byte
                while (p + 1 < n)
                {
                    if (c[p] == 'E' && c[p + 1] == 'I'
                        && (p == 0 || char.IsWhiteSpace(c[p - 1]) || c[p - 1] == 0)
                        && (p + 2 >= n || char.IsWhiteSpace(c[p + 2])))
                    {
                        break;
                    }

                    p++;
                }

                i = Math.Min(p + 2, n);
                list.Add(("BI", start, i));
                continue;
            }

            Add(start, i);
        }

        return list;

        static bool IsDelimiter(char c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';
    }

    private static string DecodeString(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return string.Empty;
        }

        if (token[0] == '<')
        {
            string hex = WhitespaceRegex().Replace(token.Trim('<', '>'), string.Empty);
            if (hex.Length % 2 == 1)
            {
                hex += "0";
            }

            var sb = new StringBuilder(hex.Length / 2);
            for (int k = 0; k + 2 <= hex.Length; k += 2)
            {
                sb.Append((char)Convert.ToInt32(hex.Substring(k, 2), 16));
            }

            return sb.ToString();
        }

        string body = token.Length >= 2 ? token[1..^1] : string.Empty;
        var outText = new StringBuilder(body.Length);
        for (int k = 0; k < body.Length; k++)
        {
            if (body[k] == '\\' && k + 1 < body.Length)
            {
                char next = body[++k];
                if (next is >= '0' and <= '7')
                {
                    int value = next - '0';
                    for (int q = 0; q < 2 && k + 1 < body.Length && body[k + 1] is >= '0' and <= '7'; q++)
                    {
                        value = (value * 8) + (body[++k] - '0');
                    }

                    outText.Append((char)value);
                }
                else
                {
                    outText.Append(next switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        'b' => '\b',
                        'f' => '\f',
                        _ => next,
                    });
                }
            }
            else
            {
                outText.Append(body[k]);
            }
        }

        return outText.ToString();
    }

    private static byte[] Inflate(byte[] raw)
    {
        using var zlib = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    [GeneratedRegex(@"trailer\s*<<(.*?)>>", RegexOptions.Singleline)]
    private static partial Regex TrailerRegex();

    [GeneratedRegex(@"startxref\s+(\d+)")]
    private static partial Regex StartXrefRegex();

    [GeneratedRegex(@"(\d+)\s+0\s+obj\b")]
    private static partial Regex ObjHeaderRegex();

    [GeneratedRegex(@"(\d+)\s+0\s+obj\s*<<")]
    private static partial Regex DictHeaderRegex();

    [GeneratedRegex(@"(\d+)\s+0\s+R")]
    private static partial Regex ObjRefRegex();

    [GeneratedRegex(@"(/[A-Za-z0-9#_.\-]+)\s+(\d+)\s+0\s+R")]
    private static partial Regex NameRefRegex();

    [GeneratedRegex(@"\((?:\\.|[^()\\])*\)|<[0-9A-Fa-f\s]*>")]
    private static partial Regex TjStringRegex();

    [GeneratedRegex(@"\s")]
    private static partial Regex WhitespaceRegex();
}
