namespace DocDr.Pdf;

/// <summary>
/// Finds a search term in one page's extracted text, in managed code, returning PDFium char
/// ranges (start index + count into the page's <c>FPDFText_GetText</c> string). Replaces running
/// <c>FPDFText_FindStart</c>/<c>FindNext</c> on every page for every query: with the page text
/// cached (<see cref="PdfPageTextCache"/>), a repeat or refined search touches PDFium only for the
/// pages that actually match.
/// <para>
/// Whitespace is flexible: any run of whitespace in the page text — including the <c>\r\n</c>
/// PDFium puts between separately positioned lines — matches any run of whitespace in the term,
/// so "design of concrete" is found when "of" ends one line and "concrete" starts the next.
/// </para>
/// </summary>
internal static class PdfTextMatcher
{
    public static List<(int Start, int Count)> Find(string text, string term, PdfSearchOptions options)
    {
        var results = new List<(int Start, int Count)>();
        string needle = CollapseWhitespace((term ?? string.Empty).Trim(), out _);
        if (needle.Length == 0 || string.IsNullOrEmpty(text))
        {
            return results;
        }

        string haystack = CollapseWhitespace(text, out int[] map);
        StringComparison comparison = (options & PdfSearchOptions.MatchCase) != 0
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        bool wholeWord = (options & PdfSearchOptions.MatchWholeWord) != 0;
        bool overlapping = (options & PdfSearchOptions.ConsecutiveMatches) != 0;

        int from = 0;
        while (from <= haystack.Length - needle.Length)
        {
            int at = haystack.IndexOf(needle, from, comparison);
            if (at < 0)
            {
                break;
            }

            int end = at + needle.Length; // exclusive, in haystack space
            if (wholeWord && !IsWordBoundary(haystack, at, end))
            {
                from = at + 1;
                continue;
            }

            int start = map[at];
            int last = map[end - 1];
            results.Add((start, last - start + 1));
            from = overlapping ? at + 1 : end;
        }

        return results;
    }

    private static bool IsWordBoundary(string s, int start, int end) =>
        (start == 0 || !char.IsLetterOrDigit(s[start - 1]))
        && (end >= s.Length || !char.IsLetterOrDigit(s[end]));

    /// <summary>
    /// Collapse every whitespace run to one space (leading/trailing runs kept as one space too, so
    /// indices stay simple), with <paramref name="map"/>[i] = the index in <paramref name="value"/>
    /// of the collapsed string's char i (a collapsed run maps to its first whitespace char).
    /// NUL chars (PDFium occasionally emits them for unmapped glyphs) count as whitespace.
    /// </summary>
    internal static string CollapseWhitespace(string value, out int[] map)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var indices = new List<int>(value.Length);
        bool inSpace = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsWhiteSpace(c) || c == '\0')
            {
                if (!inSpace)
                {
                    builder.Append(' ');
                    indices.Add(i);
                    inSpace = true;
                }

                continue;
            }

            builder.Append(c);
            indices.Add(i);
            inSpace = false;
        }

        map = indices.ToArray();
        return builder.ToString();
    }
}
