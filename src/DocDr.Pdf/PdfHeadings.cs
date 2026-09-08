using System.Text.RegularExpressions;

namespace DocDr.Pdf;

/// <summary>
/// Builds a bookmark outline for a document that has none, from detected headings — used by the
/// batch-merge flow (one node per input file, numbered sub-headings under it) and by the standalone
/// "Generate bookmarks" action.
/// </summary>
public static partial class PdfHeadings
{
    private const int MaxTitle = 110;

    /// <summary>
    /// A whole-document outline from <see cref="PdfClauses"/>' heading detection (dotted-decimal
    /// numbering plus chapter titles). Returns <c>[]</c> when fewer than two nodes are found.
    /// </summary>
    public static IReadOnlyList<PdfBookmark> FromHeadings(PdfDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfCodeStructure structure = PdfClauses.ReadStructure(document, cancellationToken);
        IReadOnlyList<PdfBookmark> tree = Map(structure.Clauses);
        return Count(tree) >= 2 ? tree : [];
    }

    /// <summary>
    /// The numbered sub-headings of one document (e.g. a single chapter about to be merged), as a
    /// flat list of leaf bookmarks with the document's own 0-based page indices. Chapter-level
    /// headings (a bare number, no dot) are dropped — the merge adds the file's own top node.
    /// </summary>
    public static IReadOnlyList<PdfBookmark> SubHeadings(PdfDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var result = new List<PdfBookmark>();
        foreach ((string number, string title, int page) in Flatten(PdfClauses.Read(document, cancellationToken)))
        {
            if (!number.Contains('.') || title.Length == 0 || LooksLikeSentence(title))
            {
                continue;
            }

            result.Add(new PdfBookmark(Clean($"{number}  {title}"), page, []));
        }

        return result;
    }

    /// <summary>The same tree with every page index shifted by <paramref name="delta"/> pages.</summary>
    public static IReadOnlyList<PdfBookmark> Shift(IReadOnlyList<PdfBookmark> tree, int delta)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree
            .Select(n => new PdfBookmark(n.Title, n.PageIndex is { } p ? p + delta : null, Shift(n.Children, delta)))
            .ToArray();
    }

    /// <summary>
    /// A suggested top-level title for a file about to be merged: its own first outline entry, else
    /// the first substantial line of its first page, else the cleaned filename.
    /// </summary>
    public static string SuggestTitle(PdfDocument document, string filePath)
    {
        ArgumentNullException.ThrowIfNull(document);

        IReadOnlyList<PdfBookmark> own = PdfBookmarks.Read(document);
        if (own.Count > 0 && !string.IsNullOrWhiteSpace(own[0].Title))
        {
            return Clean(own[0].Title);
        }

        try
        {
            foreach (string raw in PdfTextExtractor.GetPageText(document, 0).ReplaceLineEndings("\n").Split('\n'))
            {
                string line = Clean(raw);
                if (line.Length >= 3 && line.Length <= MaxTitle && line.Any(char.IsLetter))
                {
                    return line;
                }
            }
        }
        catch (PdfException)
        {
            // fall through to the filename
        }

        string stem = Path.GetFileNameWithoutExtension(filePath);
        return Clean(stem.Replace('_', ' ').Replace('-', ' '));
    }

    private static IReadOnlyList<PdfBookmark> Map(IReadOnlyList<PdfClause> clauses) =>
        clauses.Select(c => new PdfBookmark(Clean(c.Display), c.PageIndex, Map(c.Children))).ToArray();

    private static IEnumerable<(string Number, string Title, int Page)> Flatten(IReadOnlyList<PdfClause> clauses)
    {
        foreach (PdfClause c in clauses)
        {
            yield return (c.Number, c.Title, c.PageIndex);
            foreach ((string, string, int) d in Flatten(c.Children))
            {
                yield return d;
            }
        }
    }

    private static int Count(IReadOnlyList<PdfBookmark> tree) => tree.Sum(n => 1 + Count(n.Children));

    private static bool LooksLikeSentence(string title) =>
        title.Length > MaxTitle || SentenceRegex().IsMatch(title);

    private static string Clean(string? s)
    {
        s = WhitespaceRegex().Replace(s ?? string.Empty, " ").Trim();
        return s.Length > MaxTitle ? s[..MaxTitle].TrimEnd() + "…" : s;
    }

    // An exercise / problem line that leaked past clause detection ("… is subjected to …").
    [GeneratedRegex(@"(?: is subjected | is loaded | determine the | calculate the | shown (?:below|in fig) | as shown )", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SentenceRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
