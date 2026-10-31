using System.Text;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>One extracted line of a page, as the RAG chunker sees it.</summary>
/// <param name="Text">The line's text (doubled glyphs removed, maths letters normalised).</param>
/// <param name="InMargin">Every glyph sits in the page's top or bottom band — where running
/// headers, footers and page numbers live. Only such a line may be dropped as a page number.</param>
/// <param name="HasMath">The line carries maths: mathematical letters (𝜏, 𝑐), operators (⋅ √ ∑),
/// or super/subscripts — its 2-D layout (fractions, roots, indices) is flattened in plain text.</param>
internal readonly record struct ChunkLine(string Text, bool InMargin, bool HasMath);

/// <summary>A page's lines plus what extraction had to repair.</summary>
internal sealed record ChunkPage(IReadOnlyList<ChunkLine> Lines, int DoubledGlyphsRemoved)
{
    public bool IsBlank => Lines.All(l => string.IsNullOrWhiteSpace(l.Text));

    public string Text => string.Join('\n', Lines.Select(l => l.Text));
}

/// <summary>
/// Character-level page extraction for <see cref="PdfRagChunker"/>. Reads every glyph's code point
/// and box (rather than <see cref="PdfTextExtractor.GetPageText"/>'s one string) because:
/// <list type="bullet">
/// <item>Word-style equation PDFs often draw each maths glyph twice at the same spot — "𝜏𝜏Rd,c",
/// "𝛾𝛾V". A glyph repeating one of the last few kept glyphs at (nearly) the same box is dropped.</item>
/// <item>A lone "3", "d" or "v" on its own line is usually an equation fragment (an exponent, a
/// variable under a fraction bar), not a page number — but the old filter dropped them all. Line
/// boxes let the chunker only treat a line as a page number when it sits in the header / footer band.</item>
/// <item>Mathematical alphanumerics (U+1D400–1D7FF) are outside the BMP, so PDFium's char count and
/// its UTF-16 text don't line up on exactly the pages that need care; per-char code points do.</item>
/// </list>
/// </summary>
internal static class PdfChunkText
{
    /// <summary>Fraction of the page height at the top and bottom treated as the header / footer band.</summary>
    internal const double MarginBand = 0.08;

    /// <summary>How many recently kept glyphs a new one is compared with for doubling.</summary>
    private const int DuplicateWindow = 12;

    /// <summary>Intersection-over-union above which two identical glyphs are the same drawing.</summary>
    private const double DuplicateOverlap = 0.6;

    public static ChunkPage ReadPage(PdfDocument document, int pageIndex)
    {
        PdfSize size = document.GetUnrotatedPageSize(pageIndex);
        PdfPoint crop = document.GetCropOrigin(pageIndex);
        double bottom = crop.Y, top = crop.Y + size.Height;
        double band = size.Height * MarginBand;

        return document.Locked(() => PdfTextExtractor.WithTextPage(document, pageIndex, textPage =>
        {
            int count = fpdf_text.FPDFTextCountChars(textPage);
            var lines = new List<ChunkLine>();
            var line = new StringBuilder();
            double lineLow = double.MaxValue, lineHigh = double.MinValue;
            bool lineMath = false;
            var recent = new List<(int Cp, PdfRect Box)>(DuplicateWindow);
            int doubled = 0;

            void EndLine()
            {
                bool hasGlyphs = lineLow <= lineHigh;
                bool inMargin = hasGlyphs && (lineHigh <= bottom + band || lineLow >= top - band);
                lines.Add(new ChunkLine(line.ToString(), inMargin, lineMath));
                line.Clear();
                lineLow = double.MaxValue;
                lineHigh = double.MinValue;
                lineMath = false;
                recent.Clear();
            }

            for (int i = 0; i < count; i++)
            {
                int cp = (int)fpdf_text.FPDFTextGetUnicode(textPage, i);
                if (cp == '\r')
                {
                    continue;
                }

                if (cp == '\n')
                {
                    EndLine();
                    continue;
                }

                bool blank = cp == ' ' || cp == '\t' || cp == 0xA0;
                if (!blank)
                {
                    double l = 0, r = 0, b = 0, t = 0;
                    fpdf_text.FPDFTextGetCharBox(textPage, i, ref l, ref r, ref b, ref t);
                    var box = new PdfRect(l, t, r, b);

                    if (IsDuplicate(cp, box, recent))
                    {
                        doubled++;
                        continue;
                    }

                    recent.Add((cp, box));
                    if (recent.Count > DuplicateWindow)
                    {
                        recent.RemoveAt(0);
                    }

                    if (t > b)
                    {
                        lineLow = Math.Min(lineLow, b);
                        lineHigh = Math.Max(lineHigh, t);
                    }

                    lineMath |= PdfTextQuality.IsMathCodePoint(cp);
                }

                line.Append(PdfTextQuality.NormaliseMathLetter(cp));
            }

            EndLine();
            return new ChunkPage(lines, doubled);
        }));
    }

    private static bool IsDuplicate(int cp, PdfRect box, List<(int Cp, PdfRect Box)> recent)
    {
        double area = Area(box);
        if (area <= 0)
        {
            return false;
        }

        foreach ((int prevCp, PdfRect prevBox) in recent)
        {
            if (prevCp != cp)
            {
                continue;
            }

            double ix = Math.Min(box.Right, prevBox.Right) - Math.Max(box.Left, prevBox.Left);
            double iy = Math.Min(box.Top, prevBox.Top) - Math.Max(box.Bottom, prevBox.Bottom);
            if (ix <= 0 || iy <= 0)
            {
                continue;
            }

            double inter = ix * iy;
            double union = area + Area(prevBox) - inter;
            if (union > 0 && inter / union >= DuplicateOverlap)
            {
                return true;
            }
        }

        return false;
    }

    private static double Area(PdfRect r) => Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Top - r.Bottom);
}

/// <summary>
/// How far extracted text can be trusted, for <see cref="PdfRagChunker"/>'s per-chunk
/// <c>quality</c> / <c>issues</c>. A symbol font with a broken or missing character map comes
/// out as U+FFFD / private-use characters or as letters from unrelated scripts (Syriac, Oriya…)
/// — PDFium returns what the file claims, so it can't be repaired from the text layer, only flagged.
/// </summary>
internal static class PdfTextQuality
{
    /// <summary>Unicode ranges expected in technical documents written in Latin, Greek or Cyrillic
    /// script: letters, punctuation, maths and technical symbols. Anything else is "unexpected"
    /// unless the document is actually written in that script (see <see cref="ScriptProfile"/>).</summary>
    private static readonly (int Lo, int Hi)[] Expected =
    [
        (0x0009, 0x000D), (0x0020, 0x036F), (0x0370, 0x04FF), (0x1E00, 0x1EFF),
        (0x2000, 0x2BFF), (0xFB00, 0xFB06), (0x1D400, 0x1D7FF),
    ];

    public static bool IsUnmapped(int cp) =>
        cp == 0xFFFD || cp == 0 || (cp < 0x20 && cp is not ('\t' or '\n' or '\r'))
        || cp is >= 0xE000 and <= 0xF8FF || cp >= 0xF0000;

    public static bool IsExpected(int cp)
    {
        foreach ((int lo, int hi) in Expected)
        {
            if (cp >= lo && cp <= hi)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Maths letters, operators, roots, super/subscripts and the dot operator.</summary>
    public static bool IsMathCodePoint(int cp) =>
        cp is >= 0x1D400 and <= 0x1D7FF       // mathematical alphanumerics (𝜏, 𝑐, 𝛾)
            or 0x210E                          // ℎ (Planck constant — the italic h)
            or >= 0x2070 and <= 0x209F         // superscripts / subscripts
            or >= 0x2200 and <= 0x22FF         // mathematical operators (⋅ √ ∑ ≤)
            or >= 0x27C0 and <= 0x27EF
            or >= 0x2980 and <= 0x2AFF;

    /// <summary>A mathematical alphanumeric (𝜏, 𝑐, 𝐀, ℎ) → its plain letter (τ, c, A, h), so the
    /// text reads and searches normally; everything else unchanged.</summary>
    public static string NormaliseMathLetter(int cp)
    {
        if (cp is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
        {
            return "�";
        }

        string s = char.ConvertFromUtf32(cp);
        return cp is (>= 0x1D400 and <= 0x1D7FF) or 0x210E
            ? s.Normalize(NormalizationForm.FormKC)
            : s;
    }

    /// <summary>The scripts a document is really written in, so a Greek or Cyrillic standard isn't
    /// flagged for being Greek or Cyrillic: a 128-code-point block holding ≥ 20 % of the document's
    /// otherwise-unexpected letters… isn't unexpected.</summary>
    internal sealed class ScriptProfile
    {
        private readonly HashSet<int> _native;

        private ScriptProfile(HashSet<int> native) => _native = native;

        public static ScriptProfile Build(IEnumerable<string> texts)
        {
            var counts = new Dictionary<int, int>();
            long letters = 0;
            foreach (string text in texts)
            {
                foreach (Rune rune in text.EnumerateRunes())
                {
                    if (!Rune.IsLetter(rune))
                    {
                        continue;
                    }

                    letters++;
                    if (!IsExpected(rune.Value) && !IsUnmapped(rune.Value))
                    {
                        int block = rune.Value >> 7;
                        counts[block] = counts.GetValueOrDefault(block) + 1;
                    }
                }
            }

            var native = new HashSet<int>();
            foreach ((int block, int n) in counts)
            {
                if (letters > 0 && n >= letters * 0.2)
                {
                    native.Add(block);
                }
            }

            return new ScriptProfile(native);
        }

        public bool IsNative(int cp) => _native.Contains(cp >> 7);
    }

    /// <summary>Unmapped and unexpected-script character counts in <paramref name="text"/>.</summary>
    public static (int Unmapped, int Unexpected) Count(string text, ScriptProfile profile)
    {
        int unmapped = 0, unexpected = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            if (IsUnmapped(cp))
            {
                unmapped++;
            }
            else if (!IsExpected(cp) && !profile.IsNative(cp))
            {
                unexpected++;
            }
        }

        return (unmapped, unexpected);
    }

    /// <summary>A chunk's verdict: <c>suspect</c> (characters that can't be trusted), <c>check</c>
    /// (maths whose layout is flattened — verify against the page), or <c>ok</c>; with reasons.</summary>
    public static (string Quality, IReadOnlyList<string> Issues) Assess(string text, bool hasMath, ScriptProfile profile)
    {
        (int unmapped, int unexpected) = Count(text, profile);
        var issues = new List<string>();
        if (unmapped > 0)
        {
            issues.Add($"unmapped_glyphs: {unmapped} character(s) the PDF gives no meaning for (shown as � or "
                + "private-use) — usually a symbol/maths font without a usable character map; check the page image");
        }

        if (unexpected > 0)
        {
            issues.Add($"unexpected_characters: {unexpected} character(s) from scripts this document isn't written in "
                + "(e.g. Syriac, Oriya) — almost always maths symbols from a mis-mapped font; the real symbols are unknown");
        }

        if (hasMath)
        {
            issues.Add("equations: contains maths flattened to a line — fractions, roots, exponents and indices lose "
                + "their structure (e.g. a/b·c vs a/(b·c)); verify any expression against the page image");
        }

        string quality = unmapped > 0 || unexpected > 0 ? "suspect" : hasMath ? "check" : "ok";
        return (quality, issues);
    }
}
