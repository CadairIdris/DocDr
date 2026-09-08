using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DocDr.Pdf;

/// <summary>
/// Appends a document outline (bookmark tree) to an already-serialised PDF as an incremental
/// update (PDF 1.7 §7.5.6). PDFium has no outline setter and always emits a classic
/// cross-reference table with a <c>trailer</c> keyword, which this relies on — same approach as
/// <see cref="PdfMetadataWriter"/>. If the file's layout can't be parsed the bytes are returned
/// unchanged (the outline then lives only in DocDr's session).
/// </summary>
internal static partial class PdfOutlineWriter
{
    public static byte[] Append(byte[] pdf, IReadOnlyList<PdfBookmark> outline)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(outline);
        if (outline.Count == 0)
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
        int firstNewObj = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
        long prevXref = long.Parse(startXref.Groups[1].Value, CultureInfo.InvariantCulture);

        if (FindObjectBody(text, rootObj) is not { } catalogBody)
        {
            return pdf;
        }

        Match pagesRef = PagesRefRegex().Match(catalogBody);
        if (!pagesRef.Success)
        {
            return pdf;
        }

        var pageObjs = new List<int>();
        CollectPageObjects(text, int.Parse(pagesRef.Groups[1].Value, CultureInfo.InvariantCulture), pageObjs, depth: 0);
        if (pageObjs.Count == 0)
        {
            return pdf;
        }

        // Flatten the tree, assigning object numbers in visiting (depth-first) order.
        List<Item> items = BuildItems(outline);
        for (int i = 0; i < items.Count; i++)
        {
            items[i] = items[i] with { ObjNum = firstNewObj + i };
        }

        int outlinesObj = firstNewObj + items.Count;

        // Resolve parent/sibling object numbers now that every item has one.
        int ObjOf(int index) => index < 0 ? outlinesObj : items[index].ObjNum;

        using var stream = new MemoryStream(pdf.Length + (items.Count * 96) + 256);
        stream.Write(pdf);
        if (pdf.Length == 0 || pdf[^1] != (byte)'\n')
        {
            stream.WriteByte((byte)'\n');
        }

        var offsets = new List<(int Obj, long Offset)>();

        // The outline item objects.
        for (int i = 0; i < items.Count; i++)
        {
            Item it = items[i];
            int page = Math.Clamp(it.PageIndex, 0, pageObjs.Count - 1);

            var dict = new StringBuilder("<< /Title ");
            dict.Append(PdfMetadataWriter.PdfString(it.Title));
            dict.Append(CultureInfo.InvariantCulture, $" /Parent {ObjOf(it.Parent)} 0 R");
            if (it.Prev >= 0)
            {
                dict.Append(CultureInfo.InvariantCulture, $" /Prev {items[it.Prev].ObjNum} 0 R");
            }

            if (it.Next >= 0)
            {
                dict.Append(CultureInfo.InvariantCulture, $" /Next {items[it.Next].ObjNum} 0 R");
            }

            if (it.FirstChild >= 0)
            {
                dict.Append(CultureInfo.InvariantCulture,
                    $" /First {items[it.FirstChild].ObjNum} 0 R /Last {items[it.LastChild].ObjNum} 0 R /Count {it.DescendantCount}");
            }

            dict.Append(CultureInfo.InvariantCulture, $" /Dest [ {pageObjs[page]} 0 R /Fit ] >>");

            offsets.Add((it.ObjNum, stream.Length));
            WriteAscii(stream, $"{it.ObjNum} 0 obj\n{dict}\nendobj\n");
        }

        // The /Outlines dictionary.
        int firstRoot = items.FindIndex(x => x.Parent < 0);
        int lastRoot = items.FindLastIndex(x => x.Parent < 0);
        offsets.Add((outlinesObj, stream.Length));
        WriteAscii(stream,
            $"{outlinesObj} 0 obj\n<< /Type /Outlines /First {items[firstRoot].ObjNum} 0 R " +
            $"/Last {items[lastRoot].ObjNum} 0 R /Count {items.Count} >>\nendobj\n");

        // The Catalog, re-emitted with an /Outlines entry.
        string newCatalog = OutlinesKeyRegex().IsMatch(catalogBody)
            ? OutlinesKeyRegex().Replace(catalogBody, $" /Outlines {outlinesObj} 0 R")
            : $"{catalogBody.TrimEnd()} /Outlines {outlinesObj} 0 R";
        offsets.Add((rootObj, stream.Length));
        WriteAscii(stream, $"{rootObj} 0 obj\n<< {newCatalog} >>\nendobj\n");

        // One xref section with a subsection per contiguous run of object numbers.
        long xrefOffset = stream.Length;
        var sb = new StringBuilder("xref\n");
        foreach ((int start, int count) in ContiguousRuns(offsets.Select(o => o.Obj).OrderBy(n => n).ToArray()))
        {
            sb.Append(CultureInfo.InvariantCulture, $"{start} {count}\n");
            for (int n = start; n < start + count; n++)
            {
                long off = offsets.First(o => o.Obj == n).Offset;
                sb.Append(CultureInfo.InvariantCulture, $"{off:D10} 00000 n \n");
            }
        }

        int newSize = Math.Max(firstNewObj + items.Count + 1, rootObj + 1);
        sb.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {newSize} /Root {rootObj} 0 R /Prev {prevXref}");
        Match info = InfoRegex().Match(trailer.Groups[1].Value);
        if (info.Success)
        {
            sb.Append(CultureInfo.InvariantCulture, $" /Info {info.Groups[1].Value} 0 R");
        }

        sb.Append(CultureInfo.InvariantCulture, $" >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        WriteAscii(stream, sb.ToString());

        return stream.ToArray();
    }

    private readonly record struct Item(
        string Title,
        int PageIndex,
        int Parent,
        int Prev,
        int Next,
        int FirstChild,
        int LastChild,
        int DescendantCount,
        int ObjNum);

    private static List<Item> BuildItems(IReadOnlyList<PdfBookmark> roots)
    {
        var flat = new List<Item>();
        var childLists = new List<List<int>>();
        var rootChildren = new List<int>();

        void Recurse(IReadOnlyList<PdfBookmark> nodes, int parent)
        {
            List<int> siblings = parent < 0 ? rootChildren : childLists[parent];
            foreach (PdfBookmark node in nodes)
            {
                int index = flat.Count;
                siblings.Add(index);
                flat.Add(new Item(
                    Title: string.IsNullOrWhiteSpace(node.Title) ? "Untitled" : node.Title.Trim(),
                    PageIndex: node.PageIndex ?? 0,
                    Parent: parent, Prev: -1, Next: -1, FirstChild: -1, LastChild: -1,
                    DescendantCount: 0, ObjNum: 0));
                childLists.Add([]);
                Recurse(node.Children, index);
            }
        }

        Recurse(roots, -1);

        // Descendant counts, bottom-up (children always follow their parent in the flat list).
        var descendants = new int[flat.Count];
        for (int i = flat.Count - 1; i >= 0; i--)
        {
            int total = childLists[i].Count;
            foreach (int c in childLists[i])
            {
                total += descendants[c];
            }

            descendants[i] = total;
        }

        void LinkSiblings(List<int> siblings)
        {
            for (int i = 0; i < siblings.Count; i++)
            {
                flat[siblings[i]] = flat[siblings[i]] with
                {
                    Prev = i > 0 ? siblings[i - 1] : -1,
                    Next = i < siblings.Count - 1 ? siblings[i + 1] : -1,
                };
            }
        }

        LinkSiblings(rootChildren);
        for (int i = 0; i < flat.Count; i++)
        {
            List<int> kids = childLists[i];
            LinkSiblings(kids);
            flat[i] = flat[i] with
            {
                FirstChild = kids.Count > 0 ? kids[0] : -1,
                LastChild = kids.Count > 0 ? kids[^1] : -1,
                DescendantCount = descendants[i],
            };
        }

        return flat;
    }

    private static void CollectPageObjects(string pdf, int nodeObj, List<int> pages, int depth)
    {
        if (depth > 50 || pages.Count > 100_000)
        {
            return;
        }

        if (FindObjectBody(pdf, nodeObj) is not { } body)
        {
            return;
        }

        Match kids = KidsRegex().Match(body);
        if (!kids.Success)
        {
            return;
        }

        foreach (Match refMatch in RefRegex().Matches(kids.Groups[1].Value))
        {
            int child = int.Parse(refMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            if (FindObjectBody(pdf, child) is { } childBody && TypePagesRegex().IsMatch(childBody))
            {
                CollectPageObjects(pdf, child, pages, depth + 1);
            }
            else
            {
                pages.Add(child);
            }
        }
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

    private static IEnumerable<(int Start, int Count)> ContiguousRuns(int[] sorted)
    {
        int i = 0;
        while (i < sorted.Length)
        {
            int start = sorted[i];
            int j = i;
            while (j + 1 < sorted.Length && sorted[j + 1] == sorted[j] + 1)
            {
                j++;
            }

            yield return (start, j - i + 1);
            i = j + 1;
        }
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

    private static void WriteAscii(Stream stream, string s) => stream.Write(Encoding.Latin1.GetBytes(s));

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

    [GeneratedRegex(@"/Pages\s+(\d+)\s+\d+\s+R")]
    private static partial Regex PagesRefRegex();

    [GeneratedRegex(@"/Kids\s*\[([^\]]*)\]", RegexOptions.Singleline)]
    private static partial Regex KidsRegex();

    [GeneratedRegex(@"(\d+)\s+\d+\s+R")]
    private static partial Regex RefRegex();

    [GeneratedRegex(@"/Type\s*/Pages\b")]
    private static partial Regex TypePagesRegex();

    [GeneratedRegex(@"\s*/Outlines\s+\d+\s+\d+\s+R")]
    private static partial Regex OutlinesKeyRegex();
}
