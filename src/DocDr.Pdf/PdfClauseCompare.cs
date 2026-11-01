using System.Globalization;
using System.Text;

namespace DocDr.Pdf;

public enum ClauseChangeKind
{
    Unchanged,

    /// <summary>Same number, different wording (or title).</summary>
    Modified,

    /// <summary>The clause carries a different number in the new edition (its wording may also have changed).</summary>
    Renumbered,

    /// <summary>Only in the new edition.</summary>
    Added,

    /// <summary>Only in the old edition.</summary>
    Removed,
}

/// <summary>One clause matched across two editions (or present in only one).</summary>
/// <param name="Level">Nesting depth from the number: "6" → 1, "6.2.4" → 3, "Annex B" → 1.</param>
/// <param name="Similarity">Share of the clause's words unchanged: 2 × equal / (old + new). 1 = identical.</param>
/// <param name="Redline">The clause body as a redline (long unchanged runs shortened to "…").</param>
public sealed record ClauseComparison(
    ClauseChangeKind Kind,
    string? OldNumber,
    string? NewNumber,
    string? OldTitle,
    string? NewTitle,
    int? OldPage,
    int? NewPage,
    int Level,
    double Similarity,
    int WordsDeleted,
    int WordsInserted,
    IReadOnlyList<RedlineSegment> Redline)
{
    public string Number => NewNumber ?? OldNumber ?? string.Empty;

    public string Title => NewTitle ?? OldTitle ?? string.Empty;

    public bool TitleChanged => OldTitle is not null && NewTitle is not null
        && !string.Equals(CompareText.Key(OldTitle, ignoreCase: true), CompareText.Key(NewTitle, ignoreCase: true), StringComparison.Ordinal);

    /// <summary>"6.2.4", "Annex B", or "Front matter" for the text before the first heading.</summary>
    public string DisplayNumber => DisplayOf(Number);

    /// <summary>"6.2.4", "Annex B", "National Annex", or "Front matter" for an empty number.</summary>
    public static string DisplayOf(string number) =>
        number.Length == 0 ? "Front matter"
        : number.All(char.IsAsciiLetterUpper) ? (number == "NA" ? "National Annex" : $"Annex {number}")
        : number;
}

public sealed record ClauseCompareResult(IReadOnlyList<ClauseComparison> Clauses, int OldClauseCount, int NewClauseCount)
{
    public int Count(ClauseChangeKind kind) => Clauses.Count(c => c.Kind == kind);
}

/// <summary>
/// Clause-aware comparison of two editions of a standard (comparison stage 3). Each edition's cleaned
/// text (as <see cref="PdfTextCompare"/> reads it) is cut at its numbered headings — confirmed against
/// the clause tree (<see cref="PdfClauses"/>) when there is one — and clauses are matched: first by
/// number, then, for what's left (and for same-number pairs whose wording has little in common, as
/// when a new clause pushes the numbering along), by wording. Each match is word-diffed. The result
/// says per clause: unchanged, modified, renumbered (6.2.4 → 6.3.1), added or removed.
/// </summary>
public static class PdfClauseCompare
{
    /// <summary>A same-number pair below this similarity may really be two different clauses.</summary>
    private const double WeakPair = 0.45;

    /// <summary>Wording overlap needed to match clauses with different numbers.</summary>
    private const double MatchByWording = 0.55;

    private const int ShortenEqualOver = 40;

    public static ClauseCompareResult Compare(PdfDocument oldDoc, PdfDocument newDoc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldDoc);
        ArgumentNullException.ThrowIfNull(newDoc);
        List<Clause> a = Segment(CompareText.Read(oldDoc, cancellationToken), ClauseNumbers(oldDoc, cancellationToken));
        List<Clause> b = Segment(CompareText.Read(newDoc, cancellationToken), ClauseNumbers(newDoc, cancellationToken));
        return Compare(a, b, cancellationToken);
    }

    /// <summary>A clause of one edition: its heading and the token range of its body.</summary>
    internal sealed record Clause(string Number, string Title, int Page, CompareText Text, int BodyStart, int BodyEnd)
    {
        public int Level => Number.Length == 0 ? 0
            : Number.All(char.IsAsciiLetterUpper) ? 1
            : Number.Count(c => c == '.') + 1;

        public int Words => BodyEnd - BodyStart;
    }

    private static HashSet<string> ClauseNumbers(PdfDocument doc, CancellationToken ct)
    {
        PdfCodeStructure structure = doc.CachedClauseStructure ?? PdfClauses.ReadStructure(doc, ct);
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        void Walk(IEnumerable<PdfClause> clauses)
        {
            foreach (PdfClause c in clauses)
            {
                numbers.Add(c.Number);
                Walk(c.Children);
            }
        }

        Walk(structure.Clauses);
        return numbers;
    }

    /// <summary>
    /// Cut the text at its headings. With a clause tree of 3+ entries, only headings whose number it
    /// lists count (so a numbered list item or table row isn't a clause); without one, any dotted or
    /// annex heading. A number seen again later (a repeated heading, a cross-reference line) is ignored.
    /// The text before the first heading is the "front matter" clause.
    /// </summary>
    internal static List<Clause> Segment(CompareText text, IReadOnlySet<string> knownNumbers)
    {
        bool useKnown = knownNumbers.Count >= 3;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var heads = new List<CompareHeading>();
        foreach (CompareHeading h in text.Headings)
        {
            bool plausible = useKnown ? knownNumbers.Contains(h.Number) : h.Number.Contains('.') || h.Number.All(char.IsAsciiLetterUpper);
            if (plausible && seen.Add(h.Number))
            {
                heads.Add(h);
            }
        }

        var clauses = new List<Clause>();
        int first = heads.Count > 0 ? heads[0].Token : text.Tokens.Count;
        if (first > 0)
        {
            clauses.Add(new Clause(string.Empty, "Front matter", text.Tokens[0].Page, text, 0, first));
        }

        for (int i = 0; i < heads.Count; i++)
        {
            int end = i + 1 < heads.Count ? heads[i + 1].Token : text.Tokens.Count;
            clauses.Add(new Clause(heads[i].Number, heads[i].Title, heads[i].Page, text, heads[i].EndToken, Math.Max(heads[i].EndToken, end)));
        }

        return clauses;
    }

    internal static ClauseCompareResult Compare(List<Clause> a, List<Clause> b, CancellationToken ct)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int[][] ka = [.. a.Select(c => c.Text.Keys(c.BodyStart, c.BodyEnd, ids, ignoreCase: false))];
        int[][] kb = [.. b.Select(c => c.Text.Keys(c.BodyStart, c.BodyEnd, ids, ignoreCase: false))];

        var pairOfOld = new int?[a.Count];
        var pairOfNew = new int?[b.Count];
        var diffCache = new Dictionary<(int, int), (List<DiffOp> Ops, double Similarity)>();
        (List<DiffOp> Ops, double Similarity) DiffOf(int i, int j)
        {
            if (!diffCache.TryGetValue((i, j), out var d))
            {
                List<DiffOp> ops = SequenceDiff.Diff(ka[i], kb[j], ct);
                int eq = ops.Where(o => o.Kind == DiffKind.Equal).Sum(o => o.ALength);
                int total = ka[i].Length + kb[j].Length;
                diffCache[(i, j)] = d = (ops, total == 0 ? 1 : 2.0 * eq / total);
            }

            return d;
        }

        // 1. By number.
        var newByNumber = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int j = 0; j < b.Count; j++)
        {
            newByNumber.TryAdd(b[j].Number, j);
        }

        var weak = new List<(int I, int J)>();
        for (int i = 0; i < a.Count; i++)
        {
            if (newByNumber.TryGetValue(a[i].Number, out int j))
            {
                pairOfOld[i] = j;
                pairOfNew[j] = i;
                if (a[i].Number.Length > 0 && Math.Min(a[i].Words, b[j].Words) >= 12 && DiffOf(i, j).Similarity < WeakPair)
                {
                    weak.Add((i, j));
                }
            }
        }

        // 2. Weak same-number pairs are released, then everything unmatched is matched by wording.
        foreach ((int i, int j) in weak)
        {
            pairOfOld[i] = null;
            pairOfNew[j] = null;
        }

        var bagA = new HashSet<int>?[a.Count];
        var bagB = new HashSet<int>?[b.Count];
        var candidates = new List<(double Score, int I, int J)>();
        for (int i = 0; i < a.Count; i++)
        {
            if (pairOfOld[i] is not null || ka[i].Length < 6)
            {
                continue;
            }

            bagA[i] = [.. ka[i]];
            for (int j = 0; j < b.Count; j++)
            {
                if (pairOfNew[j] is not null || kb[j].Length < 6)
                {
                    continue;
                }

                bagB[j] ??= [.. kb[j]];
                double jaccard = Jaccard(bagA[i]!, bagB[j]!);
                if (jaccard >= 0.35)
                {
                    double score = DiffOf(i, j).Similarity + (SameTitle(a[i], b[j]) ? 0.1 : 0);
                    if (score >= MatchByWording)
                    {
                        candidates.Add((score, i, j));
                    }
                }
            }
        }

        foreach ((double _, int i, int j) in candidates.OrderByDescending(c => c.Score))
        {
            if (pairOfOld[i] is null && pairOfNew[j] is null)
            {
                pairOfOld[i] = j;
                pairOfNew[j] = i;
            }
        }

        // 3. Released weak pairs nobody else claimed go back together (a heavily rewritten clause).
        foreach ((int i, int j) in weak)
        {
            if (pairOfOld[i] is null && pairOfNew[j] is null)
            {
                pairOfOld[i] = j;
                pairOfNew[j] = i;
            }
        }

        // 4. Results in new-edition order, each removed clause after its old predecessor's match.
        var ordered = new List<(double Key, ClauseComparison Item)>();
        var keyOfOld = new double[a.Count];
        for (int j = 0; j < b.Count; j++)
        {
            ordered.Add((j, pairOfNew[j] is int i ? Paired(a[i], b[j], DiffOf(i, j)) : Added(b[j])));
            if (pairOfNew[j] is int oi)
            {
                keyOfOld[oi] = j;
            }
        }

        double baseKey = -1;
        int run = 0;
        for (int i = 0; i < a.Count; i++)
        {
            if (pairOfOld[i] is not null)
            {
                baseKey = keyOfOld[i];
                run = 0;
                continue;
            }

            ordered.Add((baseKey + 0.5 + (++run * 1e-6), Removed(a[i])));
        }

        List<ClauseComparison> result = [.. ordered.OrderBy(o => o.Key).Select(o => o.Item)];
        return new ClauseCompareResult(result, a.Count, b.Count);
    }

    private static bool SameTitle(Clause x, Clause y) =>
        string.Equals(CompareText.Key(x.Title, true), CompareText.Key(y.Title, true), StringComparison.Ordinal);

    private static double Jaccard(HashSet<int> x, HashSet<int> y)
    {
        if (x.Count == 0 && y.Count == 0)
        {
            return 1;
        }

        int both = x.Count < y.Count ? x.Count(y.Contains) : y.Count(x.Contains);
        return (double)both / (x.Count + y.Count - both);
    }

    private static ClauseComparison Paired(Clause o, Clause n, (List<DiffOp> Ops, double Similarity) diff)
    {
        var segments = new List<RedlineSegment>();
        int deleted = 0, inserted = 0;
        foreach (DiffOp op in diff.Ops)
        {
            switch (op.Kind)
            {
                case DiffKind.Equal:
                    AddSegment(segments, RedlineKind.Equal, Shorten(n.Text, n.BodyStart + op.BStart, n.BodyStart + op.BStart + op.BLength));
                    break;
                case DiffKind.Delete:
                    AddSegment(segments, RedlineKind.Deleted, o.Text.Join(o.BodyStart + op.AStart, o.BodyStart + op.AStart + op.ALength));
                    deleted += op.ALength;
                    break;
                default:
                    AddSegment(segments, RedlineKind.Inserted, n.Text.Join(n.BodyStart + op.BStart, n.BodyStart + op.BStart + op.BLength));
                    inserted += op.BLength;
                    break;
            }
        }

        var c = new ClauseComparison(ClauseChangeKind.Modified, o.Number, n.Number, o.Title, n.Title, o.Page, n.Page,
            n.Level, diff.Similarity, deleted, inserted, segments);
        ClauseChangeKind kind = !string.Equals(o.Number, n.Number, StringComparison.Ordinal) ? ClauseChangeKind.Renumbered
            : deleted + inserted == 0 && !c.TitleChanged ? ClauseChangeKind.Unchanged
            : ClauseChangeKind.Modified;
        return c with { Kind = kind };
    }

    private static ClauseComparison Added(Clause n) =>
        new(ClauseChangeKind.Added, null, n.Number, null, n.Title, null, n.Page, n.Level, 0, 0, n.Words,
            n.Words == 0 ? [] : [new RedlineSegment(RedlineKind.Inserted, n.Text.Join(n.BodyStart, n.BodyEnd))]);

    private static ClauseComparison Removed(Clause o) =>
        new(ClauseChangeKind.Removed, o.Number, null, o.Title, null, o.Page, null, o.Level, 0, o.Words, 0,
            o.Words == 0 ? [] : [new RedlineSegment(RedlineKind.Deleted, o.Text.Join(o.BodyStart, o.BodyEnd))]);

    /// <summary>A long unchanged run keeps its first and last words around "…".</summary>
    private static string Shorten(CompareText text, int start, int end)
    {
        const int keep = 15;
        return end - start <= ShortenEqualOver ? text.Join(start, end)
            : text.Join(start, start + keep) + " … " + text.Join(end - keep, end);
    }

    private static void AddSegment(List<RedlineSegment> segments, RedlineKind kind, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (segments.Count > 0 && segments[^1].Kind == kind)
        {
            segments[^1] = new RedlineSegment(kind, segments[^1].Text + " " + text);
        }
        else
        {
            segments.Add(new RedlineSegment(kind, text));
        }
    }

    // --- Report -------------------------------------------------------------------------------

    /// <summary>An HTML edition-comparison report: a summary table of every changed clause, then each
    /// changed clause's redline. Unchanged clauses are counted, not listed.</summary>
    public static void WriteReport(ClauseCompareResult result, string oldName, string newName, string path)
    {
        ArgumentNullException.ThrowIfNull(result);
        var html = new StringBuilder();
        PdfTextCompare.ReportHead(html, $"Clause changes — {oldName} → {newName}");
        html.Append(CultureInfo.InvariantCulture,
            $"<h1>Clause changes</h1>\n<p class=\"files\"><span class=\"del\">Old: {E(oldName)}</span><br><span class=\"ins\">New: {E(newName)}</span></p>\n");
        html.Append(CultureInfo.InvariantCulture,
            $"<p class=\"summary\">{result.OldClauseCount} clauses → {result.NewClauseCount} · {result.Count(ClauseChangeKind.Modified)} modified · {result.Count(ClauseChangeKind.Renumbered)} renumbered · {result.Count(ClauseChangeKind.Added)} added · {result.Count(ClauseChangeKind.Removed)} removed · {result.Count(ClauseChangeKind.Unchanged)} unchanged.</p>\n");
        html.Append("<p class=\"note\">Clauses are matched by number, then by wording (so a renumbered clause is followed). Running headers and footers, page numbers and licence stamps are ignored; tables and equations are compared as their extracted text.</p>\n");

        List<ClauseComparison> changed = [.. result.Clauses.Where(c => c.Kind != ClauseChangeKind.Unchanged)];
        html.Append("<table>\n<tr><th>Clause</th><th>Change</th><th>Title</th><th>Pages</th></tr>\n");
        for (int k = 0; k < changed.Count; k++)
        {
            ClauseComparison c = changed[k];
            html.Append(CultureInfo.InvariantCulture,
                $"<tr><td><a href=\"#clause-{k + 1}\">{E(NumberLabel(c))}</a></td><td><span class=\"status {c.Kind.ToString().ToLowerInvariant()}\">{KindLabel(c)}</span></td><td>{E(c.Title)}</td><td class=\"meta\">{PdfTextCompare.PagesLabel(c.OldPage, c.NewPage)}</td></tr>\n");
        }

        html.Append("</table>\n");
        for (int k = 0; k < changed.Count; k++)
        {
            ClauseComparison c = changed[k];
            html.Append(CultureInfo.InvariantCulture,
                $"<section id=\"clause-{k + 1}\">\n<h2>{E(NumberLabel(c))} {E(c.Title)} <span class=\"status {c.Kind.ToString().ToLowerInvariant()}\">{KindLabel(c)}</span> <span class=\"pages\">{PdfTextCompare.PagesLabel(c.OldPage, c.NewPage)}</span></h2>\n");
            if (c.TitleChanged)
            {
                html.Append(CultureInfo.InvariantCulture, $"<p class=\"meta\">Title: <del>{E(c.OldTitle!)}</del> <ins>{E(c.NewTitle!)}</ins></p>\n");
            }

            html.Append("<p>");
            PdfTextCompare.AppendRedline(html, c.Redline);
            html.Append("</p>\n</section>\n");
        }

        html.Append("</body>\n</html>\n");
        File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));
    }

    /// <summary>"6.2.4", or "6.2.4 → 6.3.1" for a renumbered clause.</summary>
    public static string NumberLabel(ClauseComparison c) =>
        c.Kind == ClauseChangeKind.Renumbered
            ? $"{ClauseComparison.DisplayOf(c.OldNumber!)} → {ClauseComparison.DisplayOf(c.NewNumber!)}"
            : c.DisplayNumber;

    /// <summary>"Modified (82 % same)", "Added", …</summary>
    public static string KindLabel(ClauseComparison c) => c.Kind switch
    {
        ClauseChangeKind.Unchanged => "Unchanged",
        ClauseChangeKind.Modified => string.Create(CultureInfo.InvariantCulture, $"Modified ({c.Similarity * 100:0} % same)"),
        ClauseChangeKind.Renumbered when c.WordsDeleted + c.WordsInserted == 0 => "Renumbered",
        ClauseChangeKind.Renumbered => string.Create(CultureInfo.InvariantCulture, $"Renumbered, modified ({c.Similarity * 100:0} % same)"),
        ClauseChangeKind.Added => "Added",
        _ => "Removed",
    };

    private static string E(string s) => PdfTextCompare.E(s);
}
