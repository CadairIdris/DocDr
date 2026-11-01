using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DocDr.Pdf;

/// <summary>A page-space box on one page (unrotated MediaBox space, like <see cref="PdfCharBox"/>).</summary>
public readonly record struct PageBox(int Page, PdfRect Box);

public enum RedlineKind
{
    Equal,
    Deleted,
    Inserted,
}

/// <summary>A run of words in a redline: unchanged, only in the old version, or only in the new.</summary>
public readonly record struct RedlineSegment(RedlineKind Kind, string Text);

public enum TextChangeKind
{
    /// <summary>Words only in the new version.</summary>
    Inserted,

    /// <summary>Words only in the old version.</summary>
    Deleted,

    /// <summary>Words replaced by others.</summary>
    Changed,

    /// <summary>A passage deleted in one place and inserted, word for word, in another.</summary>
    Moved,
}

/// <summary>One change between two versions' text.</summary>
/// <param name="Segments">The change as a redline, with a few unchanged words either side for context.</param>
/// <param name="OldPage">Old page the change is on (for an insertion: where it would go). Null when the old text is empty.</param>
/// <param name="NewPage">New page the change is on (for a deletion: where it was taken from).</param>
/// <param name="OldBoxes">Line boxes of the deleted words on the old pages.</param>
/// <param name="NewBoxes">Line boxes of the inserted words on the new pages.</param>
/// <param name="Clause">The clause the change falls in ("6.2.4", "B"), from the nearest numbered heading above it.</param>
/// <param name="MovedPartner">For a <see cref="TextChangeKind.Moved"/> change, the index of the other half.</param>
public sealed record TextChange(
    int Index,
    TextChangeKind Kind,
    IReadOnlyList<RedlineSegment> Segments,
    string OldText,
    string NewText,
    int? OldPage,
    int? NewPage,
    IReadOnlyList<PageBox> OldBoxes,
    IReadOnlyList<PageBox> NewBoxes,
    string? Clause,
    int? MovedPartner = null)
{
    public int WordsDeleted => CountWords(OldText);

    public int WordsInserted => CountWords(NewText);

    private static int CountWords(string s) => s.Length == 0 ? 0 : s.Count(c => c == ' ') + 1;
}

public sealed record TextCompareOptions
{
    /// <summary>Treat "Shall" and "shall" as the same word. Default false — case is meaningful in a standard.</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Changes separated by at most this many unchanged words are reported as one. Default 2.</summary>
    public int MergeGap { get; init; } = 2;

    /// <summary>Unchanged words shown either side of a change. Default 8.</summary>
    public int ContextWords { get; init; } = 8;

    /// <summary>A deleted / inserted passage needs this many words to count as moved. Default 6.</summary>
    public int MinMovedWords { get; init; } = 6;
}

public sealed record TextCompareResult(
    IReadOnlyList<TextChange> Changes,
    int OldWords,
    int NewWords,
    int WordsDeleted,
    int WordsInserted,
    int EqualWords)
{
    /// <summary>Share of the text that's unchanged: 2 × equal / (old + new). 1 = identical.</summary>
    public double Similarity => OldWords + NewWords == 0 ? 1 : 2.0 * EqualWords / (OldWords + NewWords);
}

/// <summary>
/// Word-level text comparison of two versions of a document (comparison stage 2). The text is the
/// chunk export's cleaned body text — running headers / footers, page numbers, contents pages and
/// licence stamps are dropped, a word hyphenated across a line break is re-joined — so a re-issued
/// licensed copy or a re-paginated edition doesn't show up as thousands of changes. The two word
/// sequences are diffed with a patience diff (unique words anchor the alignment, small gaps get an
/// exact LCS), and the edits are grouped into <see cref="TextChange"/>s with page boxes for
/// highlighting. Layout is ignored: only the words and their order matter.
/// </summary>
public static partial class PdfTextCompare
{
    public static TextCompareResult Compare(PdfDocument oldDoc, PdfDocument newDoc,
        TextCompareOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldDoc);
        ArgumentNullException.ThrowIfNull(newDoc);
        options ??= new TextCompareOptions();
        CompareText a = CompareText.Read(oldDoc, cancellationToken);
        CompareText b = CompareText.Read(newDoc, cancellationToken);
        return Compare(a, b, options, cancellationToken);
    }

    internal static TextCompareResult Compare(CompareText a, CompareText b, TextCompareOptions options, CancellationToken cancellationToken)
    {
        var keys = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] ka = a.Keys(keys, options.IgnoreCase);
        int[] kb = b.Keys(keys, options.IgnoreCase);
        List<DiffOp> ops = SequenceDiff.Diff(ka, kb, cancellationToken);
        List<TextChange> changes = BuildChanges(ops, a, b, options);
        int equal = ops.Where(o => o.Kind == DiffKind.Equal).Sum(o => o.ALength);
        int deleted = ops.Where(o => o.Kind != DiffKind.Equal).Sum(o => o.ALength);
        int inserted = ops.Where(o => o.Kind != DiffKind.Equal).Sum(o => o.BLength);
        return new TextCompareResult(changes, ka.Length, kb.Length, deleted, inserted, equal);
    }

    /// <summary>Group the edit script into changes: edits with ≤ <c>MergeGap</c> unchanged words
    /// between them become one change; then deleted / inserted passages with the same words are paired as moves.</summary>
    internal static List<TextChange> BuildChanges(List<DiffOp> ops, CompareText a, CompareText b, TextCompareOptions options)
    {
        // Hunks as runs of op indices.
        var hunks = new List<(int First, int Last)>();
        for (int i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == DiffKind.Equal)
            {
                continue;
            }

            if (hunks.Count > 0)
            {
                (int first, int last) = hunks[^1];
                bool smallGap = last == i - 2 && ops[i - 1].Kind == DiffKind.Equal && ops[i - 1].ALength <= options.MergeGap;
                if (last == i - 1 || smallGap)
                {
                    hunks[^1] = (first, i);
                    continue;
                }
            }

            hunks.Add((i, i));
        }

        var changes = new List<TextChange>(hunks.Count);
        foreach ((int first, int last) in hunks)
        {
            DiffOp f = ops[first], l = ops[last];
            int a0 = f.AStart, a1 = l.AStart + l.ALength, b0 = f.BStart, b1 = l.BStart + l.BLength;
            var segments = new List<RedlineSegment>();
            int ctx = Math.Max(0, options.ContextWords);
            if (ctx > 0 && b0 > 0)
            {
                Add(segments, RedlineKind.Equal, b.Join(Math.Max(0, b0 - ctx), b0));
            }

            for (int i = first; i <= last; i++)
            {
                DiffOp op = ops[i];
                if (op.Kind == DiffKind.Equal)
                {
                    Add(segments, RedlineKind.Equal, b.Join(op.BStart, op.BStart + op.BLength));
                    continue;
                }

                Add(segments, RedlineKind.Deleted, a.Join(op.AStart, op.AStart + op.ALength));
                Add(segments, RedlineKind.Inserted, b.Join(op.BStart, op.BStart + op.BLength));
            }

            if (ctx > 0 && b1 < b.Tokens.Count)
            {
                Add(segments, RedlineKind.Equal, b.Join(b1, Math.Min(b.Tokens.Count, b1 + ctx)));
            }

            string oldText = string.Join(" ", segments.Where(s => s.Kind == RedlineKind.Deleted).Select(s => s.Text));
            string newText = string.Join(" ", segments.Where(s => s.Kind == RedlineKind.Inserted).Select(s => s.Text));
            TextChangeKind kind = oldText.Length == 0 ? TextChangeKind.Inserted
                : newText.Length == 0 ? TextChangeKind.Deleted : TextChangeKind.Changed;

            var oldBoxes = new List<PageBox>();
            var newBoxes = new List<PageBox>();
            for (int i = first; i <= last; i++)
            {
                if (ops[i].Kind != DiffKind.Equal)
                {
                    oldBoxes.AddRange(a.Boxes(ops[i].AStart, ops[i].AStart + ops[i].ALength));
                    newBoxes.AddRange(b.Boxes(ops[i].BStart, ops[i].BStart + ops[i].BLength));
                }
            }

            changes.Add(new TextChange(changes.Count, kind, segments, oldText, newText,
                a.PageAt(a0, a1), b.PageAt(b0, b1), Merge(oldBoxes), Merge(newBoxes),
                b.ClauseAt(b0) ?? a.ClauseAt(a0)));
        }

        PairMoves(changes, options.MinMovedWords);
        return changes;
    }

    private static void Add(List<RedlineSegment> segments, RedlineKind kind, string text)
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

    /// <summary>Boxes on the same page that overlap vertically and touch are one highlight.</summary>
    private static List<PageBox> Merge(List<PageBox> boxes)
    {
        var result = new List<PageBox>();
        foreach (PageBox box in boxes)
        {
            if (result.Count > 0 && result[^1] is var last && last.Page == box.Page
                && Math.Min(last.Box.Top, box.Box.Top) - Math.Max(last.Box.Bottom, box.Box.Bottom) > 0.5 * Math.Min(last.Box.Height, box.Box.Height)
                && box.Box.Left <= last.Box.Right + 12 && box.Box.Right >= last.Box.Left - 12)
            {
                result[^1] = new PageBox(box.Page, new PdfRect(
                    Math.Min(last.Box.Left, box.Box.Left), Math.Max(last.Box.Top, box.Box.Top),
                    Math.Max(last.Box.Right, box.Box.Right), Math.Min(last.Box.Bottom, box.Box.Bottom)));
            }
            else
            {
                result.Add(box);
            }
        }

        return result;
    }

    /// <summary>A pure deletion and a pure insertion with the same words (≥ <paramref name="minWords"/>) are a move.</summary>
    private static void PairMoves(List<TextChange> changes, int minWords)
    {
        var inserted = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        foreach (TextChange c in changes)
        {
            if (c.Kind == TextChangeKind.Inserted && c.WordsInserted >= minWords)
            {
                string key = MoveKey(c.NewText);
                if (!inserted.TryGetValue(key, out Queue<int>? q))
                {
                    inserted[key] = q = new Queue<int>();
                }

                q.Enqueue(c.Index);
            }
        }

        foreach (TextChange c in changes.ToList())
        {
            if (c.Kind == TextChangeKind.Deleted && c.WordsDeleted >= minWords
                && inserted.TryGetValue(MoveKey(c.OldText), out Queue<int>? q) && q.Count > 0)
            {
                int partner = q.Dequeue();
                changes[c.Index] = changes[c.Index] with { Kind = TextChangeKind.Moved, MovedPartner = partner };
                changes[partner] = changes[partner] with { Kind = TextChangeKind.Moved, MovedPartner = c.Index };
            }
        }
    }

    private static string MoveKey(string text) => text.ToLowerInvariant();

    // --- Report -------------------------------------------------------------------------------

    /// <summary>An HTML change report: the summary, then every change as a redline with its pages and clause.</summary>
    public static void WriteReport(TextCompareResult result, string oldName, string newName, string path)
    {
        ArgumentNullException.ThrowIfNull(result);
        var html = new StringBuilder();
        ReportHead(html, $"Text changes — {oldName} → {newName}");
        html.Append(CultureInfo.InvariantCulture,
            $"<h1>Text changes</h1>\n<p class=\"files\"><span class=\"del\">Old: {E(oldName)}</span><br><span class=\"ins\">New: {E(newName)}</span></p>\n");
        html.Append(CultureInfo.InvariantCulture,
            $"<p class=\"summary\">{result.Changes.Count} changes · {result.WordsDeleted:N0} words deleted · {result.WordsInserted:N0} inserted · {result.Similarity:P1} of the text unchanged.</p>\n");
        html.Append("<p class=\"note\">Running headers and footers, page numbers, contents pages and licence stamps are ignored; layout is not compared.</p>\n");
        foreach (TextChange c in result.Changes)
        {
            html.Append(CultureInfo.InvariantCulture, $"<section class=\"change {c.Kind.ToString().ToLowerInvariant()}\" id=\"change-{c.Index + 1}\">\n<h2>{c.Index + 1}. {KindLabel(c)}");
            if (c.Clause is { } clause)
            {
                html.Append(CultureInfo.InvariantCulture, $" <span class=\"clause\">clause {E(clause)}</span>");
            }

            html.Append(CultureInfo.InvariantCulture, $" <span class=\"pages\">{PagesLabel(c.OldPage, c.NewPage)}</span></h2>\n<p>");
            AppendRedline(html, c.Segments);
            html.Append("</p>\n</section>\n");
        }

        html.Append("</body>\n</html>\n");
        File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));
    }

    internal static string KindLabel(TextChange c) => c.Kind switch
    {
        TextChangeKind.Inserted => "Inserted",
        TextChangeKind.Deleted => "Deleted",
        TextChangeKind.Moved when c.MovedPartner is int p => $"Moved (see {p + 1})",
        TextChangeKind.Moved => "Moved",
        _ => "Changed",
    };

    internal static string PagesLabel(int? oldPage, int? newPage) =>
        (oldPage, newPage) switch
        {
            (int o, int n) => $"old p. {o + 1} · new p. {n + 1}",
            (int o, null) => $"old p. {o + 1}",
            (null, int n) => $"new p. {n + 1}",
            _ => "",
        };

    internal static void AppendRedline(StringBuilder html, IEnumerable<RedlineSegment> segments)
    {
        bool first = true;
        foreach (RedlineSegment s in segments)
        {
            if (!first)
            {
                html.Append(' ');
            }

            first = false;
            html.Append(s.Kind switch
            {
                RedlineKind.Deleted => $"<del>{E(s.Text)}</del>",
                RedlineKind.Inserted => $"<ins>{E(s.Text)}</ins>",
                _ => $"<span class=\"ctx\">{E(s.Text)}</span>",
            });
        }
    }

    internal static void ReportHead(StringBuilder html, string title)
    {
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        html.Append(CultureInfo.InvariantCulture, $"<title>{E(title)}</title>\n");
        html.Append("""
            <style>
            body { font: 15px/1.5 system-ui, sans-serif; max-width: 60rem; margin: 2rem auto; padding: 0 1rem; color: #1f2328; }
            h1 { font-size: 1.6rem; } h2 { font-size: 1rem; margin: 1.6rem 0 .3rem; }
            .summary { font-weight: 600; } .note, .ctx, .pages, .meta { color: #656d76; } .pages, .meta { font-weight: normal; font-size: .9em; }
            .clause { background: #eef1f4; border-radius: 4px; padding: 0 .4em; font-weight: normal; }
            del, .del { color: #b42318; } del { background: #fde8e7; text-decoration: line-through; }
            ins, .ins { color: #1559b5; } ins { background: #e5efff; text-decoration: underline; }
            .status { font-size: .8em; text-transform: uppercase; letter-spacing: .04em; border-radius: 4px; padding: .1em .45em; background: #eef1f4; }
            .status.added { background: #e5efff; color: #1559b5; } .status.removed { background: #fde8e7; color: #b42318; }
            .status.modified, .status.renumbered { background: #fff4d6; color: #8a5a00; }
            table { border-collapse: collapse; } td, th { padding: .2rem .6rem; text-align: left; border-bottom: 1px solid #eef1f4; }
            </style>
            </head>
            <body>

            """);
    }

    internal static string E(string s) => WebUtility.HtmlEncode(s);
}

/// <summary>One word of a document's cleaned body text and where it was drawn.</summary>
/// <param name="Line">Index of the source line (tokens of one line share it — boxes merge by it).</param>
/// <param name="Box">The word's slice of its line box (by character position — approximate).</param>
/// <param name="Box2">A word hyphenated across a line break: the second half's box (else default).</param>
internal readonly record struct CompareToken(string Text, int Page, int Line, PdfRect Box, int Page2 = -1, PdfRect Box2 = default);

/// <summary>A numbered heading line found in the cleaned text.</summary>
/// <param name="Token">Index of the heading's first token.</param>
/// <param name="EndToken">Index just past the heading line's last token.</param>
internal sealed record CompareHeading(string Number, string Title, int Page, int Token, int EndToken);

/// <summary>A document's cleaned body text as a word sequence, plus its numbered headings.</summary>
internal sealed partial class CompareText
{
    private readonly List<CompareToken> _tokens;

    private CompareText(List<CompareToken> tokens, List<CompareHeading> headings)
    {
        _tokens = tokens;
        Headings = headings;
    }

    public IReadOnlyList<CompareToken> Tokens => _tokens;

    /// <summary>Every heading-shaped line ("6.2.4 Shear", "Annex B (informative)") in reading order.</summary>
    public List<CompareHeading> Headings { get; }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfDocument, Tuple<long, CompareText>> Cache = new();

    /// <summary>The document's comparable text — cached per document until its content changes, so the
    /// text and clause comparisons (and a re-run after Swap) extract each document once.</summary>
    public static CompareText Read(PdfDocument document, CancellationToken cancellationToken)
    {
        long version = document.ContentVersion;
        if (Cache.TryGetValue(document, out Tuple<long, CompareText>? hit) && hit.Item1 == version)
        {
            return hit.Item2;
        }

        CompareText text = Extract(document, cancellationToken);
        Cache.AddOrUpdate(document, Tuple.Create(version, text));
        return text;
    }

    private static CompareText Extract(PdfDocument document, CancellationToken cancellationToken)
    {
        PdfRagChunker.DocumentAnalysis a = PdfRagChunker.Analyse(document, cancellationToken);
        var lines = new List<(string Text, int Page, PdfRect Bounds)>();
        for (int p = 0; p < a.PageCount; p++)
        {
            if (a.Pages[p] is not { } page || a.ContentsPage[p])
            {
                continue;
            }

            foreach (ChunkLine raw in page.Lines)
            {
                if (PdfRagChunker.CleanBodyLine(raw, a.Boilerplate) is { } text)
                {
                    lines.Add((text, p, raw.Bounds));
                }
            }
        }

        return FromLines(lines);
    }

    /// <summary>Tokenise cleaned lines (pure — the tests feed lines straight in).</summary>
    internal static CompareText FromLines(IReadOnlyList<(string Text, int Page, PdfRect Bounds)> lines)
    {
        var tokens = new List<CompareToken>();
        var headings = new List<CompareHeading>();
        bool joinNext = false;
        for (int li = 0; li < lines.Count; li++)
        {
            (string text, int page, PdfRect bounds) = lines[li];
            int lineStart = tokens.Count;
            bool firstWord = true;
            foreach (Match m in Word().Matches(text))
            {
                PdfRect box = Slice(bounds, m.Index, m.Length, text.Length);
                if (firstWord && joinNext && tokens.Count > 0 && m.Value.Length > 0 && char.IsLower(m.Value[0]))
                {
                    // "rein-" + "forcement" → "reinforcement", boxed on both lines.
                    CompareToken prev = tokens[^1];
                    tokens[^1] = prev with { Text = prev.Text[..^1] + m.Value, Page2 = page, Box2 = box };
                    lineStart = tokens.Count;
                    firstWord = false;
                    continue;
                }

                firstWord = false;
                tokens.Add(new CompareToken(m.Value, page, li, box));
            }

            joinNext = HyphenatedEnd().IsMatch(text);
            if (HeadingLine(text) is { } h && tokens.Count > lineStart)
            {
                headings.Add(new CompareHeading(h.Number, h.Title, page, lineStart, tokens.Count));
            }
        }

        return new CompareText(tokens, headings);
    }

    /// <summary>"6.2.4 Shear resistance" → ("6.2.4", "Shear resistance"); "Annex B (informative)" → ("B", …);
    /// null for anything that isn't a short heading line ("1.35 Gk", a sentence starting with a number).</summary>
    internal static (string Number, string Title)? HeadingLine(string line)
    {
        if (line.Length > 140)
        {
            return null;
        }

        if (AnnexLine().Match(line) is { Success: true } annex)
        {
            return (annex.Groups[1].Value.Replace(".", string.Empty, StringComparison.Ordinal), line[annex.Length..].Trim(' ', '.', '–', '—', '-'));
        }

        if (NumberedLine().Match(line) is not { Success: true } m)
        {
            return null;
        }

        string number = m.Groups[1].Value;
        string title = m.Groups[2].Value.Trim();
        // A real heading has a word in it, isn't a sentence, and a bare chapter number needs a short title.
        bool dotted = number.Contains('.');
        if (!RealWord().IsMatch(title) || PdfRagChunker.PrintedHeading(line) is null
            || title.EndsWith('.') || (!dotted && title.Split(' ').Length > 8))
        {
            return null;
        }

        return (number, title);
    }

    private static PdfRect Slice(PdfRect line, int start, int length, int total)
    {
        if (total <= 0 || line.Width <= 0)
        {
            return line;
        }

        double l = line.Left + (line.Width * start / total);
        double r = line.Left + (line.Width * (start + length) / total);
        return new PdfRect(l, line.Top, r, line.Bottom);
    }

    public int[] Keys(Dictionary<string, int> ids, bool ignoreCase) => Keys(0, _tokens.Count, ids, ignoreCase);

    public int[] Keys(int start, int end, Dictionary<string, int> ids, bool ignoreCase)
    {
        var keys = new int[end - start];
        for (int i = start; i < end; i++)
        {
            string k = Key(_tokens[i].Text, ignoreCase);
            if (!ids.TryGetValue(k, out int id))
            {
                ids[k] = id = ids.Count;
            }

            keys[i - start] = id;
        }

        return keys;
    }

    /// <summary>Compatibility-normalised (ligatures, maths letters), quotes and dashes unified.</summary>
    internal static string Key(string word, bool ignoreCase)
    {
        string k = word.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(k.Length);
        foreach (char c in k)
        {
            sb.Append(c switch
            {
                '‘' or '’' or '‛' or '′' => '\'',
                '“' or '”' or '‟' or '″' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                _ => c,
            });
        }

        return ignoreCase ? sb.ToString().ToLowerInvariant() : sb.ToString();
    }

    public string Join(int start, int end)
    {
        if (end <= start)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            if (i > start)
            {
                sb.Append(' ');
            }

            sb.Append(_tokens[i].Text);
        }

        return sb.ToString();
    }

    public IEnumerable<PageBox> Boxes(int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            CompareToken t = _tokens[i];
            yield return new PageBox(t.Page, t.Box);
            if (t.Page2 >= 0)
            {
                yield return new PageBox(t.Page2, t.Box2);
            }
        }
    }

    /// <summary>The page of the range's first token; for an empty range, the page of the word before it.</summary>
    public int? PageAt(int start, int end)
    {
        if (_tokens.Count == 0)
        {
            return null;
        }

        int i = end > start ? start : Math.Clamp(start - 1, 0, _tokens.Count - 1);
        return _tokens[i].Page;
    }

    /// <summary>The number of the nearest heading at or before token <paramref name="index"/>.</summary>
    public string? ClauseAt(int index)
    {
        string? result = null;
        foreach (CompareHeading h in Headings)
        {
            if (h.Token > index)
            {
                break;
            }

            result = h.Number;
        }

        return result;
    }

    // A word: any run of non-space characters.
    [GeneratedRegex(@"\S+")]
    private static partial Regex Word();

    // A line ending in a word broken by a hyphen ("rein-").
    [GeneratedRegex(@"\p{Ll}-$")]
    private static partial Regex HyphenatedEnd();

    // "6", "6.2.4", "6.2.4.", "A.2", "NA.2.1" then a capital-led title (an annex letter needs a dot).
    [GeneratedRegex(@"^(\d{1,2}(?:\.\d{1,3}){0,5}|(?:NA|Z?[A-Z])(?:\.\d{1,3}){1,5})\.?\s+(\p{Lu}.*)$")]
    private static partial Regex NumberedLine();

    // "Annex B", "Annex NA (informative)", "Annex ZA — …".
    [GeneratedRegex(@"^Annex\s+(N\.?A\.?|Z?[A-Z])\b")]
    private static partial Regex AnnexLine();

    [GeneratedRegex(@"\p{L}{3,}")]
    private static partial Regex RealWord();
}

internal enum DiffKind
{
    Equal,
    Delete,
    Insert,
}

/// <summary>One step of an edit script: <c>ALength</c> words of the old sequence from <c>AStart</c>
/// and <c>BLength</c> of the new from <c>BStart</c> (equal: same length; delete: B empty; insert: A empty).</summary>
internal readonly record struct DiffOp(DiffKind Kind, int AStart, int ALength, int BStart, int BLength);

/// <summary>
/// Patience diff over integer sequences: trim the common prefix / suffix; anchor on items that occur
/// exactly once in both halves, take the longest increasing run of those anchors, and recurse into the
/// gaps between them. A gap small enough (n × m ≤ 4 M) gets an exact LCS; a large gap with no unique
/// anchors is a plain replacement. Patience keeps a moved paragraph from dragging the alignment of
/// everything around it, and stays fast on whole documents.
/// </summary>
internal static class SequenceDiff
{
    private const long MaxLcsCells = 4_000_000;

    public static List<DiffOp> Diff(int[] a, int[] b, CancellationToken cancellationToken = default)
    {
        var raw = new List<DiffOp>();
        Recurse(a, 0, a.Length, b, 0, b.Length, raw, cancellationToken);
        return Coalesce(raw);
    }

    private static void Recurse(int[] a, int a0, int a1, int[] b, int b0, int b1, List<DiffOp> ops, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int prefix = 0;
        while (a0 + prefix < a1 && b0 + prefix < b1 && a[a0 + prefix] == b[b0 + prefix])
        {
            prefix++;
        }

        if (prefix > 0)
        {
            ops.Add(new DiffOp(DiffKind.Equal, a0, prefix, b0, prefix));
            a0 += prefix;
            b0 += prefix;
        }

        int suffix = 0;
        while (a1 - suffix > a0 && b1 - suffix > b0 && a[a1 - suffix - 1] == b[b1 - suffix - 1])
        {
            suffix++;
        }

        a1 -= suffix;
        b1 -= suffix;
        Middle(a, a0, a1, b, b0, b1, ops, ct);
        if (suffix > 0)
        {
            ops.Add(new DiffOp(DiffKind.Equal, a1, suffix, b1, suffix));
        }
    }

    private static void Middle(int[] a, int a0, int a1, int[] b, int b0, int b1, List<DiffOp> ops, CancellationToken ct)
    {
        int n = a1 - a0, m = b1 - b0;
        if (n == 0 && m == 0)
        {
            return;
        }

        if (n == 0 || m == 0)
        {
            ops.Add(new DiffOp(n == 0 ? DiffKind.Insert : DiffKind.Delete, a0, n, b0, m));
            return;
        }

        List<(int A, int B)> anchors = UniqueAnchors(a, a0, a1, b, b0, b1);
        if (anchors.Count == 0)
        {
            if ((long)n * m <= MaxLcsCells)
            {
                Lcs(a, a0, a1, b, b0, b1, ops);
            }
            else
            {
                ops.Add(new DiffOp(DiffKind.Delete, a0, n, b0, 0));
                ops.Add(new DiffOp(DiffKind.Insert, a1, 0, b0, m));
            }

            return;
        }

        int pa = a0, pb = b0;
        foreach ((int ai, int bi) in anchors)
        {
            Recurse(a, pa, ai, b, pb, bi, ops, ct);
            ops.Add(new DiffOp(DiffKind.Equal, ai, 1, bi, 1));
            pa = ai + 1;
            pb = bi + 1;
        }

        Recurse(a, pa, a1, b, pb, b1, ops, ct);
    }

    /// <summary>Items occurring exactly once in each range, as (a index, b index), reduced to the
    /// longest run increasing in both (patience sorting).</summary>
    private static List<(int A, int B)> UniqueAnchors(int[] a, int a0, int a1, int[] b, int b0, int b1)
    {
        var countA = new Dictionary<int, (int Count, int Index)>();
        for (int i = a0; i < a1; i++)
        {
            countA[a[i]] = countA.TryGetValue(a[i], out var c) ? (c.Count + 1, c.Index) : (1, i);
        }

        var countB = new Dictionary<int, (int Count, int Index)>();
        for (int j = b0; j < b1; j++)
        {
            if (countA.TryGetValue(b[j], out var ca) && ca.Count == 1)
            {
                countB[b[j]] = countB.TryGetValue(b[j], out var c) ? (c.Count + 1, c.Index) : (1, j);
            }
        }

        var pairs = new List<(int A, int B)>();
        for (int i = a0; i < a1; i++)
        {
            if (countA[a[i]].Count == 1 && countB.TryGetValue(a[i], out var cb) && cb.Count == 1)
            {
                pairs.Add((i, cb.Index));
            }
        }

        // Longest increasing subsequence on B (pairs are already in A order).
        if (pairs.Count == 0)
        {
            return pairs;
        }

        var tails = new List<int>();          // index into pairs of the smallest tail for each length
        var previous = new int[pairs.Count];
        for (int i = 0; i < pairs.Count; i++)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (pairs[tails[mid]].B < pairs[i].B)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            previous[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count)
            {
                tails.Add(i);
            }
            else
            {
                tails[lo] = i;
            }
        }

        var result = new List<(int A, int B)>(tails.Count);
        for (int i = tails[^1]; i >= 0; i = previous[i])
        {
            result.Add(pairs[i]);
        }

        result.Reverse();
        return result;
    }

    /// <summary>Exact LCS by dynamic programming (for small gaps).</summary>
    private static void Lcs(int[] a, int a0, int a1, int[] b, int b0, int b1, List<DiffOp> ops)
    {
        int n = a1 - a0, m = b1 - b0;
        var len = new int[(n + 1) * (m + 1)];
        int W = m + 1;
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                len[(i * W) + j] = a[a0 + i] == b[b0 + j]
                    ? len[((i + 1) * W) + j + 1] + 1
                    : Math.Max(len[((i + 1) * W) + j], len[(i * W) + j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[a0 + x] == b[b0 + y])
            {
                ops.Add(new DiffOp(DiffKind.Equal, a0 + x, 1, b0 + y, 1));
                x++;
                y++;
            }
            else if (len[((x + 1) * W) + y] >= len[(x * W) + y + 1])
            {
                ops.Add(new DiffOp(DiffKind.Delete, a0 + x, 1, b0 + y, 0));
                x++;
            }
            else
            {
                ops.Add(new DiffOp(DiffKind.Insert, a0 + x, 0, b0 + y, 1));
                y++;
            }
        }

        if (x < n)
        {
            ops.Add(new DiffOp(DiffKind.Delete, a0 + x, n - x, b0 + y, 0));
        }

        if (y < m)
        {
            ops.Add(new DiffOp(DiffKind.Insert, a0 + n, 0, b0 + y, m - y));
        }
    }

    /// <summary>Merge neighbouring ops of the same kind; order deletes before inserts within a run of edits.</summary>
    private static List<DiffOp> Coalesce(List<DiffOp> raw)
    {
        var result = new List<DiffOp>();
        int i = 0;
        while (i < raw.Count)
        {
            if (raw[i].Kind == DiffKind.Equal)
            {
                DiffOp e = raw[i++];
                while (i < raw.Count && raw[i].Kind == DiffKind.Equal)
                {
                    e = e with { ALength = e.ALength + raw[i].ALength, BLength = e.BLength + raw[i].BLength };
                    i++;
                }

                result.Add(e);
                continue;
            }

            // A run of edits: one delete (if any) then one insert (if any).
            int aStart = raw[i].AStart, bStart = raw[i].BStart, aLen = 0, bLen = 0;
            while (i < raw.Count && raw[i].Kind != DiffKind.Equal)
            {
                aLen += raw[i].ALength;
                bLen += raw[i].BLength;
                i++;
            }

            if (aLen > 0)
            {
                result.Add(new DiffOp(DiffKind.Delete, aStart, aLen, bStart, 0));
            }

            if (bLen > 0)
            {
                result.Add(new DiffOp(DiffKind.Insert, aStart + aLen, 0, bStart, bLen));
            }
        }

        return result;
    }
}
