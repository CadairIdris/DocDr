using System.Text;
using System.Text.RegularExpressions;

namespace DocDr.Pdf;

/// <summary>A clickable textual cross-reference found in a page's text ("see 6.2.5", "Annex L").</summary>
public sealed record PdfCrossRef(PdfRect Rect, int TargetPageIndex, string Label);

/// <summary>
/// Finds cued in-text references to other clauses ("see 6.2.5", "in accordance with 8.3.1"), to
/// annexes ("Annex L"), and to figures / tables ("Figure 8.5", "Table 4.3"), and resolves them to a
/// page using the <see cref="PdfCodeStructure"/> map (clause numbers plus <c>"Figure 8.5"</c> /
/// <c>"Table 4.3"</c> caption keys). Coordinates are in unrotated page space, matching
/// <see cref="PdfLink"/>. Heuristic and deliberately conservative — a reference is only returned when
/// its target resolves exactly (figures / tables) or up the dotted prefix (clauses).
/// </summary>
public static partial class PdfCrossReferences
{
    // "see 6.2.5", "in accordance with 8.3.1", "given in 5.1.4(3)" — a cue then a dotted number.
    [GeneratedRegex(
        @"(?:see|See|according to|in accordance with|given in|specified in|defined in|as per|refer to|Clause|clause)\s+(\d{1,2}(?:\.\d{1,3}){1,4})(?:\s*\(\d{1,3}\))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex ClauseRef();

    // "Annex L", "Annex NA" — resolved to the annex's first page.
    [GeneratedRegex(@"Annex\s+(N\.?A\.?|Z?[A-Z])\b", RegexOptions.CultureInvariant)]
    private static partial Regex AnnexRef();

    // "Figure 8.5", "Table A.3", "Figure 8.3a" — resolved against the caption map (exact match only).
    [GeneratedRegex(@"(Figure|Table)\s+([A-Z]{0,2}\.?\d{1,2}(?:\.\d{1,3})?[a-z]?)", RegexOptions.CultureInvariant)]
    private static partial Regex CaptionRef();

    /// <summary>Flatten a clause tree to a number → first-page map for <see cref="Scan"/>.</summary>
    public static IReadOnlyDictionary<string, int> BuildPageMap(IReadOnlyList<PdfClause> clauses)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Walk(IReadOnlyList<PdfClause> nodes)
        {
            foreach (PdfClause c in nodes)
            {
                map.TryAdd(c.Number, c.PageIndex);
                Walk(c.Children);
            }
        }

        Walk(clauses);
        return map;
    }

    /// <summary>The full lookup for <see cref="Scan"/>: clause numbers plus figure / table captions.</summary>
    public static IReadOnlyDictionary<string, int> BuildPageMap(PdfCodeStructure structure)
    {
        ArgumentNullException.ThrowIfNull(structure);
        var map = new Dictionary<string, int>(BuildPageMap(structure.Clauses), StringComparer.OrdinalIgnoreCase);
        foreach ((string key, int page) in structure.Captions)
        {
            map[key] = page;
        }

        return map;
    }

    public static IReadOnlyList<PdfCrossRef> Scan(
        PdfDocument document, int pageIndex, IReadOnlyDictionary<string, int> clausePages)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(clausePages);
        if (clausePages.Count == 0)
        {
            return [];
        }

        IReadOnlyList<PdfCharBox> chars = PdfTextExtractor.GetCharBoxes(document, pageIndex);
        if (chars.Count == 0)
        {
            return [];
        }

        // Concatenate the characters, remembering which char box each string offset came from.
        var sb = new StringBuilder(chars.Count);
        var offsetToChar = new List<int>(chars.Count);
        for (int i = 0; i < chars.Count; i++)
        {
            string t = string.IsNullOrEmpty(chars[i].Text) ? " " : chars[i].Text;
            foreach (char ch in t)
            {
                sb.Append(char.IsControl(ch) ? ' ' : ch);
                offsetToChar.Add(i);
            }
        }

        string text = sb.ToString();
        var found = new List<PdfCrossRef>();
        var claimed = new List<(int Start, int End)>();

        void Emit(Match m, int? target, string label)
        {
            if (target is not int page || page == pageIndex)
            {
                return;
            }

            int start = m.Index;
            int end = m.Index + m.Length;
            if (claimed.Any(c => start < c.End && end > c.Start))
            {
                return;
            }

            claimed.Add((start, end));
            PdfRect box = chars[offsetToChar[start]].Box;
            for (int k = start + 1; k < end; k++)
            {
                box = Union(box, chars[offsetToChar[k]].Box);
            }

            found.Add(new PdfCrossRef(box, page, label));
        }

        foreach (Match m in ClauseRef().Matches(text))
        {
            string num = m.Groups[1].Value;
            Emit(m, Resolve(num, clausePages), $"Clause {num}");
        }

        foreach (Match m in AnnexRef().Matches(text))
        {
            string letter = m.Groups[1].Value.Replace(".", string.Empty).ToUpperInvariant();
            Emit(m, clausePages.TryGetValue(letter, out int p) ? p : null, $"Annex {letter}");
        }

        foreach (Match m in CaptionRef().Matches(text))
        {
            string key = $"{m.Groups[1].Value} {m.Groups[2].Value}";
            Emit(m, clausePages.TryGetValue(key, out int p) ? p : null, key);
        }

        return found;
    }

    /// <summary>Exact match, else walk up the dotted prefixes (8.3.1 → 8.3 → 8) for a clause number.</summary>
    private static int? Resolve(string number, IReadOnlyDictionary<string, int> map)
    {
        string n = number;
        while (true)
        {
            if (map.TryGetValue(n, out int page))
            {
                return page;
            }

            int dot = n.LastIndexOf('.');
            if (dot <= 0)
            {
                return null;
            }

            n = n[..dot];
        }
    }

    private static PdfRect Union(PdfRect a, PdfRect b) => new(
        Math.Min(a.Left, b.Left),
        Math.Max(a.Top, b.Top),
        Math.Max(a.Right, b.Right),
        Math.Min(a.Bottom, b.Bottom));
}
