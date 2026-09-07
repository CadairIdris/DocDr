using System.Text.RegularExpressions;

namespace DocDr.Pdf;

/// <summary>A detected clause / section heading in a design code or standard.</summary>
public sealed class PdfClause
{
    public required string Number { get; init; }

    public required string Title { get; init; }

    /// <summary>Nesting depth, 1 for a top-level clause.</summary>
    public int Level { get; init; }

    /// <summary>Zero-based page the heading is on.</summary>
    public int PageIndex { get; init; }

    public IReadOnlyList<PdfClause> Children { get; init; } = [];

    /// <summary>True for a bare annex letter ("B", "NA") — displayed as "Annex B".</summary>
    private bool IsAnnexRoot =>
        Number.Length is 1 or 2 && Number.All(char.IsAsciiLetterUpper);

    /// <summary>"6.4.3  Design of connections" — number + title.</summary>
    public string Display
    {
        get
        {
            string number = IsAnnexRoot ? (Number == "NA" ? "National Annex" : $"Annex {Number}") : Number;
            return string.IsNullOrEmpty(Title) ? number : $"{number}  {Title}";
        }
    }
}

/// <summary>
/// The navigable structure of a design code: the clause tree plus a <c>"Figure 8.5"</c> /
/// <c>"Table 4.3"</c> → page map built from caption lines, for resolving figure / table references.
/// </summary>
public sealed record PdfCodeStructure(
    IReadOnlyList<PdfClause> Clauses,
    IReadOnlyDictionary<string, int> Captions)
{
    public static readonly PdfCodeStructure Empty =
        new([], new Dictionary<string, int>());
}

/// <summary>
/// Best-effort clause-heading detection for standards / design codes, tuned for the dotted-decimal
/// numbering used by the Eurocodes and BS EN / ISO (e.g. <c>6.4.3</c>, <c>A.2.1</c>). Works off
/// PDFium's reading-order page text (<see cref="PdfTextExtractor.GetPageText"/>), which is reliable
/// even on files whose per-character extraction is noisy. Heuristic — see the Stage 8 spec notes;
/// letter-section schemes (AISC <c>D1.2a</c>) are not covered. The same page pass also collects
/// figure / table caption lines (<see cref="ReadStructure"/>).
/// </summary>
public static partial class PdfClauses
{
    // "6", "6.4", "6.4.3.2" then whitespace then a capital-led title.
    [GeneratedRegex(@"^(\d{1,2}(?:\.\d{1,3}){0,5})\s+(\p{Lu}[^\n]{1,120}?)\s*$")]
    private static partial Regex NumericHeading();

    // Annex clauses: "Annex A", "Annex NA", "A.1", "B.2.3", "NA.2.5", "ZA.1".
    [GeneratedRegex(@"^(?:(?<a>Annex\s+(?<al>N\.?A\.?|Z?[A-Z]))\s*$|(?<b>(?:N\.?A\.?|Z?[A-Z])(?:\.\d{1,3}){1,4})\s+(?<t>\p{Lu}[^\n]{1,120}?))\s*$")]
    private static partial Regex AnnexHeading();

    // Obvious running header / footer / boilerplate lines.
    [GeneratedRegex(@"^(?:EN\s|BS\s|ISO\s|prEN\s|Sect\.|Page\s|©|Copyright|Provided by|Licensee|No reproduction|Not for Resale|American Institute)")]
    private static partial Regex NoiseLine();

    // A table-of-contents / list-of-figures entry: "... dotted leader ... 123".
    [GeneratedRegex(@"\.\s?\.\s?\.\s?\.|\.{2,}\s*\d{1,4}\s*$")]
    private static partial Regex TocLeader();

    // A "title" that is really running body text that merely starts with a number.
    [GeneratedRegex(@"(?::| according to | in accordance with | as given in | as defined in | as specified in | shall | should | may be | is the | are the | of the )")]
    private static partial Regex SentenceLike();

    // A figure / table caption: "Figure 8.5 — …", "Table A.3 : …" at the start of a line.
    [GeneratedRegex(@"^(Figure|Table)\s+([A-Z]{0,2}\.?\d{1,2}(?:\.\d{1,3})?[a-z]?)\s*[-‐‒–—:]", RegexOptions.CultureInvariant)]
    private static partial Regex CaptionLine();

    public static IReadOnlyList<PdfClause> Read(PdfDocument document, CancellationToken cancellationToken = default) =>
        ReadStructure(document, cancellationToken).Clauses;

    /// <summary>The clause tree and the figure / table caption map, from one pass over the page text.</summary>
    public static PdfCodeStructure ReadStructure(PdfDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var flat = new List<PdfClause>();
        var captions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int page = 0; page < document.PageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = PdfTextExtractor.GetPageText(document, page);
            }
            catch (PdfException)
            {
                continue;
            }

            string[] lines = text.ReplaceLineEndings("\n").Split('\n');

            // A contents / list-of-tables page: entries wrap, so leaders don't always sit on the
            // heading's own line — skip the whole page rather than trust individual lines.
            if (lines.Count(l => TocLeader().IsMatch(l)) >= 5)
            {
                continue;
            }

            // NoiseLine + TocLeader + the structural checks carry the filtering — no positional skip
            // (a heading can be the first or last line on its page).
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length < 3 || line.Length > 150 || NoiseLine().IsMatch(line) || TocLeader().IsMatch(line))
                {
                    continue;
                }

                if (CaptionLine().Match(line) is { Success: true } caption)
                {
                    // First sighting wins — the caption comes before any later "see Figure 8.5".
                    captions.TryAdd($"{Capitalise(caption.Groups[1].Value)} {caption.Groups[2].Value}", page);
                    continue;
                }

                if (TryHeading(line, page) is { } clause)
                {
                    flat.Add(clause);
                }
            }
        }

        return new PdfCodeStructure(BuildTree(Deduplicate(flat)), captions);
    }

    private static string Capitalise(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();

    private static PdfClause? TryHeading(string line, int page)
    {
        Match m = NumericHeading().Match(line);
        string number, title;

        if (m.Success)
        {
            number = m.Groups[1].Value;
            title = CollapseWhitespace(m.Groups[2].Value);
            if (int.TryParse(number.Split('.')[0], out int top) && top > 30)
            {
                return null; // "318 Section 21.9.6" and the like
            }

            // A no-dot number ("13 Precast concrete elements") is only a chapter heading if the
            // title reads like one — several Title-Case words, not a fragment of a sentence.
            if (!number.Contains('.') && !LooksLikeChapterTitle(title))
            {
                return null;
            }
        }
        else
        {
            m = AnnexHeading().Match(line);
            if (!m.Success)
            {
                return null;
            }

            if (m.Groups["a"].Success)
            {
                number = NormaliseAnnexLetter(m.Groups["al"].Value);
                title = string.Empty;
            }
            else
            {
                string[] seg = m.Groups["b"].Value.Split('.');
                number = string.Join('.', new[] { NormaliseAnnexLetter(seg[0]) }.Concat(seg[1..]));
                title = CollapseWhitespace(m.Groups["t"].Value);
            }
        }

        if (title.Length > 0 && (SentenceLike().IsMatch($" {title} ") || (title.EndsWith('.') && title.Length > 25)))
        {
            return null;
        }

        return new PdfClause
        {
            Number = number,
            Title = title,
            Level = number.Count(c => c == '.') + 1,
            PageIndex = page,
        };
    }

    private static readonly string[] DanglingWords =
        ["or", "and", "to", "the", "of", "a", "an", "in", "with", "for", "as", "by", "on"];

    private static readonly string[] SentenceStarters = ["For", "Where", "When", "If", "This", "The", "These", "It"];

    private static bool LooksLikeChapterTitle(string title)
    {
        if (title.Length < 4 || title.EndsWith('.') || title.Any(char.IsDigit))
        {
            return false;
        }

        if (title.Count(c => c == '(') != title.Count(c => c == ')'))
        {
            return false; // truncated table cell: "... (including uneven blin"
        }

        string[] words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 1 or > 12)
        {
            return false;
        }

        if (DanglingWords.Contains(words[^1], StringComparer.OrdinalIgnoreCase) ||
            SentenceStarters.Contains(words[0]))
        {
            return false; // a fragment of running text, not a heading
        }

        return true;
    }

    private static string NormaliseAnnexLetter(string s) => s.Replace(".", string.Empty).ToUpperInvariant();

    /// <summary>First sighting of each clause number wins (headings recur in running headers).</summary>
    private static List<PdfClause> Deduplicate(List<PdfClause> flat)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PdfClause>();
        foreach (PdfClause c in flat)
        {
            if (seen.Add(c.Number))
            {
                result.Add(c);
            }
        }

        return result;
    }

    private static IReadOnlyList<PdfClause> BuildTree(List<PdfClause> detected)
    {
        // Synthesise any missing prefix ancestors so the tree is well formed
        // (e.g. we saw 9.1 but never a bare "9").
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nodes = new List<PdfClause>();

        void Upsert(PdfClause c)
        {
            if (index.TryGetValue(c.Number, out int at))
            {
                // Fill a synthesised (title-less) node from a real sighting — but only one at or
                // near its first subclause's page, so a stray caption further in doesn't hijack it.
                if (nodes[at].Title.Length == 0 && c.Title.Length > 0 && c.PageIndex <= nodes[at].PageIndex + 1)
                {
                    nodes[at] = c;
                }
            }
            else
            {
                index[c.Number] = nodes.Count;
                nodes.Add(c);
            }
        }

        foreach (PdfClause c in detected)
        {
            if (c.Number.Contains('.'))
            {
                string[] parts = c.Number.Split('.');
                for (int n = 1; n < parts.Length; n++)
                {
                    string prefix = string.Join('.', parts[..n]);
                    if (!index.ContainsKey(prefix))
                    {
                        Upsert(new PdfClause { Number = prefix, Title = string.Empty, Level = n, PageIndex = c.PageIndex });
                    }
                }
            }

            Upsert(c);
        }

        var kids = nodes.ToDictionary(c => c.Number, _ => new List<PdfClause>(), StringComparer.OrdinalIgnoreCase);
        var roots = new List<PdfClause>();

        foreach (PdfClause c in nodes)
        {
            string? parent = ParentNumber(c.Number);
            if (parent is not null && kids.ContainsKey(parent))
            {
                kids[parent].Add(c);
            }
            else
            {
                roots.Add(c);
            }
        }

        PdfClause Attach(PdfClause c) => new()
        {
            Number = c.Number, Title = c.Title, Level = c.Level, PageIndex = c.PageIndex,
            Children = kids[c.Number].OrderBy(k => k, ClauseOrder.Instance).Select(Attach).ToArray(),
        };

        return roots.OrderBy(r => r, RootOrder.Instance).Select(Attach).ToArray();
    }

    private static string? ParentNumber(string number)
    {
        int dot = number.LastIndexOf('.');
        return dot > 0 ? number[..dot] : null;
    }

    private static string CollapseWhitespace(string s) => WhitespaceRun().Replace(s, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    /// <summary>Orders siblings by their last numeric/alpha segment (1.2 before 1.10, A before B).</summary>
    private sealed class ClauseOrder : IComparer<PdfClause>
    {
        public static readonly ClauseOrder Instance = new();

        public int Compare(PdfClause? x, PdfClause? y)
        {
            string sx = LastSegment(x!.Number);
            string sy = LastSegment(y!.Number);
            if (int.TryParse(sx, out int nx) && int.TryParse(sy, out int ny))
            {
                return nx.CompareTo(ny);
            }

            return string.CompareOrdinal(sx, sy);
        }

        private static string LastSegment(string number)
        {
            int dot = number.LastIndexOf('.');
            return dot >= 0 ? number[(dot + 1)..] : number;
        }
    }

    /// <summary>Numeric chapters (0, 1, 2 …) first, in order; then annex letters (A, B, NA …).</summary>
    private sealed class RootOrder : IComparer<PdfClause>
    {
        public static readonly RootOrder Instance = new();

        public int Compare(PdfClause? x, PdfClause? y)
        {
            (int rank, int num, string txt) Key(PdfClause c) =>
                int.TryParse(c.Number, out int n) ? (0, n, string.Empty) : (1, 0, c.Number);

            (int rank, int num, string txt) kx = Key(x!), ky = Key(y!);
            int r = kx.rank.CompareTo(ky.rank);
            if (r != 0)
            {
                return r;
            }

            return kx.rank == 0 ? kx.num.CompareTo(ky.num) : string.CompareOrdinal(kx.txt, ky.txt);
        }
    }
}
