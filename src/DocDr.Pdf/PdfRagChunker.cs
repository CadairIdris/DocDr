using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocDr.Pdf;

/// <summary>
/// One retrieval-sized passage of a document, ready to embed for RAG. Serialised with snake_case
/// keys (<c>source_path</c>, <c>page_start</c>, …). <see cref="Id"/> is stable across re-exports;
/// <see cref="TokenEstimate"/> is <c>Text.Length / 4</c>.
/// </summary>
public sealed record RagChunk(
    string Id,
    string Text,
    string SourcePath,
    string DocTitle,
    int PageStart,
    int PageEnd,
    string SectionTitle,
    int ChunkIndex,
    int TokenEstimate);

/// <summary>Result of a chunk run: the chunks plus what could not be processed.</summary>
public sealed record RagChunkResult(
    IReadOnlyList<RagChunk> Chunks,
    IReadOnlyList<int> PagesWithoutText,
    int SectionCount);

/// <summary>Tuning for <see cref="PdfRagChunker.Chunk"/>. Token counts are approximated as chars / 4.</summary>
public sealed record RagChunkOptions
{
    /// <summary>Target chunk size in tokens (clamped to 128–4000). Default 600.</summary>
    public int TargetTokens { get; init; } = 600;

    /// <summary>Fraction of a chunk repeated at the start of the next one (clamped to 0–0.5). Default 0.15.</summary>
    public double OverlapFraction { get; init; } = 0.15;

    /// <summary>A chunk below this many tokens is only emitted when it is the tail of a section. Default 48.</summary>
    public int MinChunkTokens { get; init; } = 48;

    /// <summary>Prefix each chunk's <c>text</c> with "<c>{doc title} — {section}</c>" so the embedding
    /// carries where the passage sits (contextual-retrieval-lite). Default true.</summary>
    public bool IncludeSectionHeading { get; init; } = true;

    internal int MaxChars => Math.Clamp(TargetTokens, 48, 4000) * 4;

    internal int OverlapChars => (int)(MaxChars * Math.Clamp(OverlapFraction, 0, 0.5));

    internal int MinChars => Math.Max(1, MinChunkTokens) * 4;
}

/// <summary>
/// Splits a text-layer PDF into overlapping, section-aware chunks for retrieval-augmented generation
/// and writes them as JSONL. Section boundaries come from the detected clause tree
/// (<see cref="PdfClauses"/>) when the document has one, else the bookmark outline
/// (<see cref="PdfBookmarks"/>), else the whole document. Running headers / footers and bare page
/// numbers are stripped; wrapped lines are joined and words de-hyphenated across line breaks.
/// Pages with no extractable text are reported, not chunked (full coverage waits on OCR).
/// </summary>
public static partial class PdfRagChunker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"^\s*(?:\d{1,4}|[ivxlcdm]{1,7}|page\s+\d{1,4}|p\.\s*\d{1,4})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PageNumberLine();

    // Line that opens a new block: a list bullet, "(a)", "1.", or a clause number.
    [GeneratedRegex(@"^(?:[-–—•·*]\s|\(\w{1,4}\)\s|\d{1,3}[.)]\s|\d+\.\d)")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"(?<=[.!?;:])\s+")]
    private static partial Regex SentenceBreak();

    // A contents / list-of-tables leader: "…… 243" or a run of dots.
    [GeneratedRegex(@"\.\s?\.\s?\.\s?\.|\.{2,}\s*\d{1,4}\s*$")]
    private static partial Regex TocLeader();

    public static RagChunkResult Chunk(
        PdfDocument document, RagChunkOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new RagChunkOptions();

        int pageCount = document.PageCount;
        var pageText = new string?[pageCount];
        var pagesWithoutText = new List<int>();
        for (int p = 0; p < pageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string t = PdfTextExtractor.GetPageText(document, p);
                pageText[p] = string.IsNullOrWhiteSpace(t) ? null : t;
            }
            catch (PdfException)
            {
                pageText[p] = null;
            }

            if (pageText[p] is null)
            {
                pagesWithoutText.Add(p);
            }
        }

        string[] sectionForPage = SectionForEachPage(document, pageCount, cancellationToken);
        int sectionCount = 1;
        for (int p = 1; p < pageCount; p++)
        {
            if (!string.Equals(sectionForPage[p], sectionForPage[p - 1], StringComparison.Ordinal))
            {
                sectionCount++;
            }
        }

        HashSet<string> boilerplate = DetectBoilerplate(pageText);

        string sourcePath = document.FilePath ?? string.Empty;
        string docTitle = DocumentTitle(document);

        var paragraphs = new List<Paragraph>();
        for (int p = 0; p < pageCount; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pageText[p] is not { } text)
            {
                continue;
            }

            string[] rawLines = text.ReplaceLineEndings("\n").Split('\n');
            if (rawLines.Count(l => TocLeader().IsMatch(l)) >= 5)
            {
                continue; // a contents / list-of-figures page — navigation, not knowledge
            }

            foreach (string para in ParagraphsFromPage(rawLines, boilerplate))
            {
                paragraphs.Add(new Paragraph(para, p, sectionForPage[p]));
            }
        }

        var chunks = BuildChunks(paragraphs, options, sourcePath, docTitle);
        return new RagChunkResult(chunks, pagesWithoutText, pageCount == 0 ? 0 : sectionCount);
    }

    /// <summary>Serialise chunks as JSON Lines (one object per line, UTF-8, no BOM).</summary>
    public static void WriteJsonl(IEnumerable<RagChunk> chunks, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(writer);
        foreach (RagChunk chunk in chunks)
        {
            writer.WriteLine(JsonSerializer.Serialize(chunk, JsonOptions));
        }
    }

    public static void WriteJsonl(IEnumerable<RagChunk> chunks, string path)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        WriteJsonl(chunks, writer);
    }

    private readonly record struct Paragraph(string Text, int Page, string Section);

    // --- Section boundaries --------------------------------------------------------------

    private static string[] SectionForEachPage(PdfDocument document, int pageCount, CancellationToken ct)
    {
        var result = new string[pageCount];
        if (pageCount == 0)
        {
            return result;
        }

        List<(int Page, string Title)> markers = ClauseMarkers(document, ct);
        if (markers.Count < 3)
        {
            markers = BookmarkMarkers(document);
        }

        markers.Sort((a, b) => a.Page.CompareTo(b.Page));

        string current = "Front matter";
        int mi = 0;
        for (int p = 0; p < pageCount; p++)
        {
            while (mi < markers.Count && markers[mi].Page <= p)
            {
                if (!string.IsNullOrWhiteSpace(markers[mi].Title))
                {
                    current = markers[mi].Title.Trim();
                }

                mi++;
            }

            result[p] = current;
        }

        return result;
    }

    private static List<(int, string)> ClauseMarkers(PdfDocument document, CancellationToken ct)
    {
        var flat = new List<(int, string)>();
        void Walk(IReadOnlyList<PdfClause> nodes)
        {
            foreach (PdfClause c in nodes)
            {
                flat.Add((c.PageIndex, c.Display));
                Walk(c.Children);
            }
        }

        try
        {
            Walk(PdfClauses.Read(document, ct));
        }
        catch (PdfException)
        {
            // fall through to bookmarks
        }

        return flat;
    }

    private static List<(int, string)> BookmarkMarkers(PdfDocument document)
    {
        var flat = new List<(int, string)>();
        void Walk(IReadOnlyList<PdfBookmark> nodes)
        {
            foreach (PdfBookmark b in nodes)
            {
                if (b.PageIndex is int page)
                {
                    flat.Add((page, b.Title));
                }

                Walk(b.Children);
            }
        }

        try
        {
            Walk(PdfBookmarks.Read(document));
        }
        catch (PdfException)
        {
            // no outline — caller falls back to a single section
        }

        return flat;
    }

    private static string DocumentTitle(PdfDocument document)
    {
        try
        {
            string title = PdfMetadata.GetInfo(document).Title;
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title.Trim();
            }
        }
        catch (PdfException)
        {
            // fall through
        }

        return document.FilePath is { Length: > 0 } path
            ? Path.GetFileNameWithoutExtension(path)
            : "Untitled";
    }

    // --- Cleaning -----------------------------------------------------------------------

    private static HashSet<string> DetectBoilerplate(string?[] pageText)
    {
        int pages = pageText.Count(t => t is not null);
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (pages < 4)
        {
            return result;
        }

        int threshold = Math.Max(3, (int)Math.Ceiling(pages * 0.5));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string? text in pageText)
        {
            if (text is null)
            {
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in text.ReplaceLineEndings("\n").Split('\n'))
            {
                // No length cap: body text does not recur verbatim across many pages, so any line
                // (even a long legal footer that PDFium extracts as one run) that does is chrome.
                string norm = Normalise(raw);
                if (norm.Length > 0 && seen.Add(norm))
                {
                    counts[norm] = counts.GetValueOrDefault(norm) + 1;
                }
            }
        }

        foreach ((string line, int count) in counts)
        {
            if (count >= threshold)
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static string Normalise(string line) => WhitespaceRun().Replace(line, " ").Trim();

    private static IEnumerable<string> ParagraphsFromPage(string[] rawLines, HashSet<string> boilerplate)
    {
        var lines = new List<string>();
        foreach (string raw in rawLines)
        {
            string line = Normalise(raw);
            if (line.Length == 0 || boilerplate.Contains(line)
                || PageNumberLine().IsMatch(line) || TocLeader().IsMatch(line))
            {
                continue;
            }

            lines.Add(line);
        }

        var paragraphs = new List<string>();
        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            if (sb.Length == 0)
            {
                sb.Append(line);
                continue;
            }

            char last = sb[^1];
            if (last == '-' && sb.Length >= 2 && char.IsLetter(sb[^2]) && char.IsLower(line[0]))
            {
                sb.Length -= 1;
                sb.Append(line); // de-hyphenate across the line break
            }
            else if (BlockStart().IsMatch(line) || last is '.' or '!' or '?' || LooksLikeHeading(sb.ToString()))
            {
                paragraphs.Add(sb.ToString());
                sb.Clear();
                sb.Append(line);
            }
            else
            {
                sb.Append(' ').Append(line); // wrapped line — join
            }
        }

        if (sb.Length > 0)
        {
            paragraphs.Add(sb.ToString());
        }

        return paragraphs;
    }

    private static bool LooksLikeHeading(string line) =>
        line.Length < 70 && !line.EndsWith('.') && ClauseHeadingShape().IsMatch(line);

    [GeneratedRegex(@"^(?:\d+(?:\.\d+)*|Annex\s+\w+|[A-Z]\.\d)\s+\p{Lu}")]
    private static partial Regex ClauseHeadingShape();

    // --- Windowing ---------------------------------------------------------------------

    private static IReadOnlyList<RagChunk> BuildChunks(
        List<Paragraph> paragraphs, RagChunkOptions opt, string sourcePath, string docTitle)
    {
        int overlapChars = opt.OverlapChars;
        // Leave room for the "{title} — {section}" prefix so the finished text still lands ≤ MaxChars.
        int headingReserve = opt.IncludeSectionHeading ? 160 : 0;
        int maxChars = Math.Max(200, opt.MaxChars - headingReserve);
        // Fill only to (max − overlap) so a chunk plus the tail carried into the next stays ≤ max.
        int budget = Math.Max(200, maxChars - overlapChars);
        int minChars = Math.Min(opt.MinChars, budget);
        string idStem = sourcePath is { Length: > 0 }
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : "chunk";

        // 1. Flatten paragraphs to units that are each no longer than the budget.
        var units = new List<Paragraph>();
        foreach (Paragraph para in paragraphs)
        {
            if (para.Text.Length <= budget)
            {
                units.Add(para);
                continue;
            }

            foreach (string sentence in SentenceBreak().Split(para.Text))
            {
                string s = sentence.Trim();
                if (s.Length == 0)
                {
                    continue;
                }

                if (s.Length <= budget)
                {
                    units.Add(para with { Text = s });
                    continue;
                }

                for (int i = 0; i < s.Length; i += budget)
                {
                    units.Add(para with { Text = s.Substring(i, Math.Min(budget, s.Length - i)) });
                }
            }
        }

        // 2. Pack units into chunks; seed each new chunk with a short character tail of the last.
        var chunks = new List<RagChunk>();
        var buf = new StringBuilder();
        int realUnits = 0;
        int firstPage = 0, lastPage = 0, index = 0;
        string section = "Front matter";

        void Emit(bool force)
        {
            string body = Normalise(buf.ToString());
            if (realUnits == 0 || body.Length == 0 || (!force && body.Length < minChars))
            {
                return;
            }

            string text = opt.IncludeSectionHeading && !string.IsNullOrEmpty(section)
                ? $"{docTitle} — {section}\n\n{body}"
                : body;

            chunks.Add(new RagChunk(
                $"{idStem}#c{index:D4}", text, sourcePath, docTitle,
                firstPage, lastPage, section, index, text.Length / 4));
            index++;

            buf.Clear();
            realUnits = 0;
            if (overlapChars > 0 && text.Length > overlapChars)
            {
                string tail = text[^overlapChars..];
                int space = tail.IndexOf(' ');
                buf.Append(space > 0 ? tail[(space + 1)..] : tail).Append(' ');
                firstPage = lastPage; // the carried tail belongs to the last page seen
            }
        }

        foreach (Paragraph u in units)
        {
            if (realUnits > 0 && buf.Length + u.Text.Length + 1 > maxChars)
            {
                Emit(force: false);
            }

            if (realUnits == 0 && buf.Length == 0)
            {
                firstPage = u.Page;
            }

            if (realUnits == 0)
            {
                section = u.Section;
            }

            buf.Append(u.Text).Append(' ');
            lastPage = u.Page;
            realUnits++;
        }

        Emit(force: true);
        return chunks;
    }
}
