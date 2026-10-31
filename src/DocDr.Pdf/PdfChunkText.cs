using System.Text;
using System.Text.RegularExpressions;
using PDFiumCore;

namespace DocDr.Pdf;

/// <summary>One extracted line of a page, as the RAG chunker sees it.</summary>
/// <param name="Text">The line's text (doubled glyphs removed, maths letters normalised).</param>
/// <param name="InMargin">Every glyph sits in the page's top or bottom band — where running
/// headers, footers and page numbers live. Only such a line may be dropped as a page number.</param>
/// <param name="HasMath">The line carries maths: mathematical letters (𝜏, 𝑐), operators (⋅ √ ∑),
/// super/subscripts or assembled bracket / fraction-bar pieces — its 2-D layout is flattened.</param>
/// <param name="Bounds">Union of the line's glyph boxes (page space); zero-size when it has none.</param>
internal readonly record struct ChunkLine(string Text, bool InMargin, bool HasMath, PdfRect Bounds = default);

/// <summary>A numbered display equation found on a page: the "(8.94)" label at the right margin
/// plus the lines drawn in its band.</summary>
/// <param name="Label">The label as printed, e.g. "(8.94)".</param>
/// <param name="Bounds">Union of the equation's glyph boxes and its label (page space).</param>
/// <param name="RawText">The equation as extracted (label excluded), in drawing order.</param>
/// <param name="Stacked">Parts of it sit on different baselines (a fraction, root or stacked
/// index), which plain text can't represent — the linear text is unreliable.</param>
internal sealed record ChunkEquation(string Label, PdfRect Bounds, string RawText, bool Stacked);

/// <summary>A page's lines plus what extraction had to repair.</summary>
internal sealed record ChunkPage(IReadOnlyList<ChunkLine> Lines, int DoubledGlyphsRemoved)
{
    public bool IsBlank => Lines.All(l => string.IsNullOrWhiteSpace(l.Text));

    public string Text => string.Join('\n', Lines.Select(l => l.Text));

    public IReadOnlyList<ChunkEquation> Equations { get; init; } = [];

    /// <summary>The page box the glyphs were measured against (CropBox in page space).</summary>
    public PdfRect PageBox { get; init; }
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
/// <item>Mathematical alphanumerics (U+1D400–1D7FF) are outside the BMP: PDFium reports each as two
/// chars (one per UTF-16 surrogate) that must be re-paired — see <see cref="BuildPage"/>.</item>
/// <item>Numbered display equations are located (label + box) so a consumer can crop the page image
/// — see <see cref="FindEquations"/>.</item>
/// </list>
/// </summary>
internal static partial class PdfChunkText
{
    /// <summary>Fraction of the page height at the top and bottom treated as the header / footer band.</summary>
    internal const double MarginBand = 0.08;

    /// <summary>Intersection-over-union above which two identical glyphs are the same drawing.</summary>
    private const double DuplicateOverlap = 0.5;

    /// <summary>One PDFium text-page char: its UTF-16 unit (FPDFText_GetUnicode) and box.</summary>
    internal readonly record struct RawChar(int Unit, PdfRect Box);

    /// <summary>An equation label: "(8.94)", "(O.7)", "(6.2.a)", "(6.2a)", "(B.11)", "(6.3N)", and a book's
    /// own "(D6.17)". Exactly one dot between numbers, so paragraph numbers "(3)" and clause references
    /// "(5.3.2)" — margin pointers to the clause a part replaces — aren't labels; it must stand alone ("4.3.2(1)" is a
    /// clause-paragraph reference). Letters may touch it — PDFium doesn't always put a space
    /// before a far-off label — the position test in <see cref="FindEquations"/> decides.</summary>
    [GeneratedRegex(@"(?<![\d.])\(\s*(?:[A-Z]{1,2}\d{0,2}|\d{1,3})\.\d{1,3}(?:\.?[a-z])?N?\s*\)(?!\()")]
    private static partial Regex EquationLabel();

    // A word of 4+ lowercase letters — two or more make a line prose, not an equation fragment.
    [GeneratedRegex(@"\b\p{Ll}{4,}\b")]
    private static partial Regex ProseWord();

    public static ChunkPage ReadPage(PdfDocument document, int pageIndex)
    {
        PdfSize size = document.GetUnrotatedPageSize(pageIndex);
        PdfPoint crop = document.GetCropOrigin(pageIndex);

        RawChar[] chars = document.Locked(() => PdfTextExtractor.WithTextPage(document, pageIndex, textPage =>
        {
            int count = fpdf_text.FPDFTextCountChars(textPage);
            var raw = new RawChar[Math.Max(0, count)];
            for (int i = 0; i < count; i++)
            {
                double l = 0, r = 0, b = 0, t = 0;
                fpdf_text.FPDFTextGetCharBox(textPage, i, ref l, ref r, ref b, ref t);
                int unit = (int)fpdf_text.FPDFTextGetUnicode(textPage, i);
                if (unit == 0xA5 && IsSymbolFont(textPage, i))
                {
                    unit = 0xD7; // "¥": a maths-pi font's × read through the Latin-1 fallback
                }

                raw[i] = new RawChar(unit, new PdfRect(l, t, r, b));
            }

            return raw;
        }));

        return BuildPage(chars, bottom: crop.Y, top: crop.Y + size.Height, left: crop.X, right: crop.X + size.Width);
    }

    /// <summary>A symbolic / maths font (Mathematical Pi, Symbol, MT Extra…): its codes aren't
    /// Latin-1, so a "¥" from it is really the font's multiplication sign.</summary>
    private static bool IsSymbolFont(FpdfTextpageT textPage, int index)
    {
        int flags = 0;
        uint length = fpdf_text.FPDFTextGetFontInfo(textPage, index, IntPtr.Zero, 0, ref flags);
        string name = string.Empty;
        if (length is > 1 and < 512)
        {
            IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)length);
            try
            {
                fpdf_text.FPDFTextGetFontInfo(textPage, index, buffer, length, ref flags);
                name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }

        const int Symbolic = 4; // PDF font descriptor flag bit 3
        return (flags & Symbolic) != 0
            || Regex.IsMatch(name, "Pi|Symbol|Math|Extra|Universal", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Turn PDFium's chars into lines. Pure (no PDFium), so it's tested directly with real char
    /// sequences.
    /// <list type="bullet">
    /// <item>PDFium returns a character outside the BMP (𝑘 U+1D458) as <b>two chars, one per UTF-16
    /// surrogate</b>, sharing the glyph's box — they're re-paired into one code point here.</item>
    /// <item>A code point repeating one of the last few kept on the line at (nearly) the same box is
    /// dropped: Word's maths export writes ToUnicode entries like
    /// <c>&lt;0747&gt; &lt;D835DC58D835DC58&gt;</c> — one glyph, "𝑘𝑘" — and fake-bold / overprinted text
    /// draws glyphs twice. The one exception is a second 'f' at one box: an "ff" / "ffi" ligature glyph
    /// legitimately expands to two f's ("effect").</item>
    /// <item>Adobe Symbol / MT Extra private-use pieces get their meaning: assembled bracket pieces
    /// (top / extender / bottom) → one "(" / ")" / "[" / "]" / "{" / "}", the horizontal extender used
    /// as a fraction bar → "/". U+0002 between two letters (a hyphen glyph some fonts leave unmapped)
    /// → "-"; non-breaking / Unicode hyphens (U+2010–2012) → "-".</item>
    /// </list>
    /// </summary>
    internal static ChunkPage BuildPage(IReadOnlyList<RawChar> chars, double bottom, double top,
        double left = double.NaN, double right = double.NaN)
    {
        double band = (top - bottom) * MarginBand;
        var lines = new List<ChunkLine>();
        var glyphsPerLine = new List<List<(int TextIndex, PdfRect Box)>>();
        var line = new StringBuilder();
        var lineGlyphs = new List<(int TextIndex, PdfRect Box)>();
        double lineLow = double.MaxValue, lineHigh = double.MinValue;
        double lineLeft = double.MaxValue, lineRight = double.MinValue;
        bool lineMath = false;
        // Every glyph kept on the page, by code point: an overprint / fake-bold pass is often a
        // separate run that PDFium reports as a new line, so the check can't stop at the line.
        var kept = new Dictionary<int, List<PdfRect>>();
        (int Cp, PdfRect Box) lastKept = (-1, default);
        double letterHeight = 0; // height of the last kept letter/digit — a hyphen glyph is much flatter
        int doubled = 0;
        int lastPiece = 0; // family of the bracket / bar piece just emitted (0 = none)

        void EndLine()
        {
            bool hasGlyphs = lineLow <= lineHigh;
            bool inMargin = hasGlyphs && (lineHigh <= bottom + band || lineLow >= top - band);
            PdfRect bounds = hasGlyphs ? new PdfRect(lineLeft, lineHigh, lineRight, lineLow) : default;
            lines.Add(new ChunkLine(line.ToString(), inMargin, lineMath, bounds));
            glyphsPerLine.Add([.. lineGlyphs]);
            line.Clear();
            lineGlyphs.Clear();
            lineLow = lineLeft = double.MaxValue;
            lineHigh = lineRight = double.MinValue;
            lineMath = false;
            lastPiece = 0;
        }

        for (int i = 0; i < chars.Count; i++)
        {
            int cp = chars[i].Unit;
            PdfRect box = chars[i].Box;

            // Re-pair a surrogate pair split across two PDFium chars; a lone half is unrecoverable.
            if (cp is >= 0xD800 and <= 0xDBFF && i + 1 < chars.Count && chars[i + 1].Unit is >= 0xDC00 and <= 0xDFFF)
            {
                cp = char.ConvertToUtf32((char)cp, (char)chars[i + 1].Unit);
                i++;
            }
            else if (cp is >= 0xD800 and <= 0xDFFF)
            {
                cp = 0xFFFD;
            }

            if (cp == '\r')
            {
                continue;
            }

            if (cp == '\n')
            {
                EndLine();
                continue;
            }

            string text;
            bool blank = cp == ' ' || cp == '\t' || cp == 0xA0;
            if (blank)
            {
                text = char.ConvertFromUtf32(cp);
                lastPiece = 0;
            }
            else
            {
                if (IsDuplicate(cp, box, kept, lastKept))
                {
                    doubled++;
                    continue;
                }

                if (!kept.TryGetValue(cp, out List<PdfRect>? boxes))
                {
                    kept[cp] = boxes = [];
                }

                boxes.Add(box);
                lastKept = (cp, box);

                cp = cp switch
                {
                    0xF8E8 or 0xF6DA => 0x00AE, // Symbol's sans / serif ®
                    0xF8E9 or 0xF6D9 => 0x00A9, // ©
                    0xF8EA or 0xF6DB => 0x2122, // ™
                    _ => cp,
                };
                (string? piece, int family) = SymbolPiece(cp);
                if (piece is { Length: 0 })
                {
                    lineMath = true;
                    continue; // an extender: no text, and it doesn't break the bracket it lengthens
                }

                if (piece is not null)
                {
                    lineMath = true;
                    if (family == lastPiece)
                    {
                        continue; // the next piece of the same assembled bracket / bar
                    }

                    lastPiece = family;
                    text = piece;
                }
                else
                {
                    lastPiece = 0;
                    if (cp is > 0 and < 0x20 and not ('\t' or '\n' or '\r'))
                    {
                        // An unmapped glyph code. A flat, dash-shaped glyph between two letters / digits
                        // is a hyphen ("y\x02direction" → "y-direction"); anything else (a ligature, a
                        // symbol) can't be known — make it a visible, counted "�", not an invisible control.
                        bool flat = box.Height >= 0 && letterHeight > 0 && box.Height <= letterHeight * 0.45;
                        bool between = line.Length > 0 && char.IsLetterOrDigit(line[^1])
                            && i + 1 < chars.Count && chars[i + 1].Unit is < 0xD800 or > 0xDFFF
                            && char.IsLetterOrDigit((char)chars[i + 1].Unit);
                        cp = flat && between ? '-' : 0xFFFD;
                    }
                    else if (cp < 0x10000 && char.IsLetterOrDigit((char)cp) && box.Height > 0)
                    {
                        letterHeight = box.Height;
                    }

                    text = cp is 0x2010 or 0x2011 or 0x2012 ? "-" : PdfTextQuality.NormaliseMathLetter(cp);
                    lineMath |= PdfTextQuality.IsMathCodePoint(cp);
                }

                if (box.Top >= box.Bottom && box.Right >= box.Left)
                {
                    lineLow = Math.Min(lineLow, box.Bottom);
                    lineHigh = Math.Max(lineHigh, box.Top);
                    lineLeft = Math.Min(lineLeft, box.Left);
                    lineRight = Math.Max(lineRight, box.Right);
                    lineGlyphs.Add((line.Length, box));
                }
            }

            line.Append(text);
        }

        EndLine();
        return new ChunkPage(lines, doubled)
        {
            Equations = FindEquations(lines, glyphsPerLine, left, right),
            PageBox = new PdfRect(double.IsNaN(left) ? 0 : left, top, double.IsNaN(right) ? 0 : right, bottom),
        };
    }

    /// <summary>Adobe Symbol / MT Extra private-use glyphs with a standard meaning → (text, family);
    /// consecutive pieces of one family are one symbol. (null, 0) for anything else.</summary>
    private static (string? Text, int Family) SymbolPiece(int cp) => cp switch
    {
        >= 0xF8EB and <= 0xF8ED or >= 0x239B and <= 0x239D => ("(", 1),
        >= 0xF8F6 and <= 0xF8F8 or >= 0x239E and <= 0x23A0 => (")", 2),
        >= 0xF8EE and <= 0xF8F0 or >= 0x23A1 and <= 0x23A3 => ("[", 3),
        >= 0xF8F9 and <= 0xF8FB or >= 0x23A4 and <= 0x23A6 => ("]", 4),
        >= 0xF8F1 and <= 0xF8F3 or >= 0x23A7 and <= 0x23A9 => ("{", 5),
        >= 0xF8FC and <= 0xF8FE or >= 0x23AB and <= 0x23AD => ("}", 6),
        // Extenders that only lengthen another glyph (√'s vinculum, an arrow's or integral's shaft, a
        // brace's middle run) carry no text of their own.
        0xF8E5 or 0xF8E6 or 0xF8F4 or 0xF8F5 or 0x23AA or 0x23AE => (string.Empty, 8),
        0xF8E7 or 0x23AF => ("/", 7), // horizontal extender — in an equation, a fraction bar
        _ => (null, 0),
    };

    private static bool IsDuplicate(int cp, PdfRect box, Dictionary<int, List<PdfRect>> kept, (int Cp, PdfRect Box) lastKept)
    {
        if (!kept.TryGetValue(cp, out List<PdfRect>? boxes))
        {
            return false;
        }

        int same = boxes.Count(b => SameSpot(box, b));
        // "ff" / "ffi" / "ffl" ligatures expand to two f's at one box, one straight after the other —
        // keep that second f (but not an f from a later overprint pass).
        bool ligature = cp == 'f' && same == 1 && lastKept.Cp == 'f' && SameSpot(box, lastKept.Box);
        return same >= 1 && !ligature;
    }

    /// <summary>The same drawing position: box IoU ≥ 0.5, or — for the zero-height boxes some fonts
    /// report — the same baseline and ≥ 50 % horizontal overlap.</summary>
    private static bool SameSpot(PdfRect a, PdfRect b)
    {
        double ix = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (ix <= 0)
        {
            return false;
        }

        double areaA = Area(a), areaB = Area(b);
        if (areaA <= 0 || areaB <= 0)
        {
            double union = Math.Max(a.Right, b.Right) - Math.Min(a.Left, b.Left);
            return Math.Abs(a.Bottom - b.Bottom) < 0.5 && union > 0 && ix / union >= DuplicateOverlap;
        }

        double iy = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        if (iy <= 0)
        {
            return false;
        }

        double inter = ix * iy;
        double all = areaA + areaB - inter;
        return all > 0 && inter / all >= DuplicateOverlap;
    }

    private static double Area(PdfRect r) => Math.Max(0, r.Right - r.Left) * Math.Max(0, r.Top - r.Bottom);

    /// <summary>
    /// Numbered display equations: a line ending in a label like "(8.94)" that sits in the right part
    /// of the page and isn't the end of a prose sentence ("…given in (8.94)"). The equation is that
    /// line plus the neighbouring lines (in drawing order) whose vertical centre lies within ~1.6
    /// label heights of the label — a fraction's numerator and denominator, a stacked exponent —
    /// stopping at prose or another label. <see cref="ChunkEquation.Stacked"/> when those parts sit
    /// on different baselines: PDFium emits them as separate lines in drawing order, without the
    /// fraction bar, so the linear text can't be trusted.
    /// </summary>
    private static IReadOnlyList<ChunkEquation> FindEquations(
        List<ChunkLine> lines, List<List<(int TextIndex, PdfRect Box)>> glyphs, double left, double right)
    {
        var result = new List<ChunkEquation>();
        for (int li = 0; li < lines.Count; li++)
        {
            // The label is the rightmost thing on its line (wherever the stream put it in the text),
            // sits in the right part of the page, and is set off by a clear gap — "…given in (8.94)"
            // at the end of a prose line has only a word space before it.
            Match? found = null;
            List<PdfRect> labelBoxes = [];
            PdfRect label = default;
            foreach (Match candidate in EquationLabel().Matches(lines[li].Text))
            {
                int end = candidate.Index + candidate.Length;
                var candidateBoxes = glyphs[li].Where(g => g.TextIndex >= candidate.Index && g.TextIndex < end).Select(g => g.Box).ToList();
                if (candidateBoxes.Count == 0)
                {
                    continue;
                }

                PdfRect u = Union(candidateBoxes);
                double lh = u.Height > 1 ? u.Height : 10;
                bool rightmost = !glyphs[li].Any(g => (g.TextIndex < candidate.Index || g.TextIndex >= end)
                    && g.Box.Left >= u.Right - 0.5);
                bool rightSide = double.IsNaN(left) || double.IsNaN(right) || right <= left
                    || u.Left >= left + ((right - left) * 0.55);
                var leftOf = glyphs[li].Where(g => (g.TextIndex < candidate.Index || g.TextIndex >= end)
                    && g.Box.Right <= u.Left + 0.5).Select(g => g.Box.Right).ToList();
                bool gap = leftOf.Count == 0 || u.Left - leftOf.Max() >= 0.8 * lh;
                if (rightmost && rightSide && gap)
                {
                    found = candidate;
                    labelBoxes = candidateBoxes;
                    label = u;
                }
            }

            if (found is not { } m)
            {
                continue;
            }

            string before = (lines[li].Text[..m.Index] + " " + lines[li].Text[(m.Index + m.Length)..]).Trim();

            double h = label.Height > 1 ? label.Height : 10;
            double cy = (label.Top + label.Bottom) / 2;
            bool InBand(int k) =>
                lines[k].Bounds.Height >= 0 && lines[k].Bounds.Width > 0
                && Math.Abs(((lines[k].Bounds.Top + lines[k].Bounds.Bottom) / 2) - cy) <= 1.6 * h
                && !HasLabel(lines[k].Text) && !IsProse(lines[k].Text)
                && !string.IsNullOrWhiteSpace(lines[k].Text);

            int first = li, last = li;
            while (first - 1 >= 0 && InBand(first - 1))
            {
                first--;
            }

            while (last + 1 < lines.Count && InBand(last + 1))
            {
                last++;
            }

            var parts = new List<string>();
            var centres = new List<double>();
            var boxes = new List<PdfRect>(labelBoxes);
            for (int k = first; k <= last; k++)
            {
                IEnumerable<(int TextIndex, PdfRect Box)> body = k == li
                    ? glyphs[k].Where(g => g.TextIndex < m.Index || g.TextIndex >= m.Index + m.Length)
                    : glyphs[k];
                var bodyBoxes = body.Select(g => g.Box).ToList();
                string text = (k == li ? before : lines[k].Text).Trim();
                if (text.Length > 0)
                {
                    parts.Add(text);
                }

                if (bodyBoxes.Count > 0)
                {
                    boxes.AddRange(bodyBoxes);
                    PdfRect u = Union(bodyBoxes);
                    centres.Add((u.Top + u.Bottom) / 2);
                }
            }

            if (parts.Count == 0)
            {
                continue;
            }

            bool stacked = centres.Count > 1 && centres.Max() - centres.Min() > 0.5 * h;
            string raw = string.Join(' ', parts);
            result.Add(new ChunkEquation(Regex.Replace(m.Value, @"\s+", string.Empty), Union(boxes), raw, stacked));
        }

        return result;
    }

    private static bool HasLabel(string text) => EquationLabel().IsMatch(text);

    private static bool IsProse(string text) => ProseWord().Count(text) >= 2;

    private static PdfRect Union(IReadOnlyList<PdfRect> boxes) => new(
        boxes.Min(b => b.Left), boxes.Max(b => b.Top), boxes.Max(b => b.Right), boxes.Min(b => b.Bottom));
}

/// <summary>
/// How far extracted text can be trusted, for <see cref="PdfRagChunker"/>'s per-chunk
/// <c>quality</c> / <c>issues</c>. A symbol font with a broken or missing character map comes
/// out as U+FFFD / private-use characters or as letters from unrelated scripts (Syriac, Oriya…)
/// — PDFium returns what the file claims, so it can't be repaired from the text layer, only flagged.
/// </summary>
internal static partial class PdfTextQuality
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
    public static (string Quality, IReadOnlyList<string> Issues) Assess(string text, bool hasMath, ScriptProfile profile) =>
        Assess(text, hasMath, profile, []);

    /// <inheritdoc cref="Assess(string, bool, ScriptProfile)"/>
    /// <param name="equations">The chunk's located equations; any not <c>check</c> makes it suspect.</param>
    public static (string Quality, IReadOnlyList<string> Issues) Assess(
        string text, bool hasMath, ScriptProfile profile, IReadOnlyList<RagEquation> equations)
    {
        (int unmapped, int unexpected) = Count(text, profile);
        var issues = new List<string>();
        bool suspect = unmapped > 0 || unexpected > 0;
        if (unmapped > 0)
        {
            issues.Add($"unmapped_glyphs: {unmapped} character(s) the PDF gives no meaning for (shown as �, "
                + "private-use or control characters) — usually a symbol/maths font without a usable character map; "
                + "check the page image");
        }

        if (unexpected > 0)
        {
            issues.Add($"unexpected_characters: {unexpected} character(s) from scripts this document isn't written in "
                + "(e.g. Syriac, Oriya) — almost always maths symbols from a mis-mapped font; the real symbols are unknown");
        }

        IReadOnlyList<string> repeated = RepeatedLetterTokens(text);
        if (repeated.Count > 0)
        {
            suspect = true;
            issues.Add($"duplicated_glyphs: {string.Join(", ", repeated)} — a letter repeated three or more times is "
                + "almost always a subscript / maths glyph the font maps to the wrong or a repeated letter "
                + "(fccc for fck, MEEEE for MEd); the real symbols are unknown, check the page image");
        }

        var bad = equations.Where(e => e.Quality == "suspect").ToList();
        if (bad.Count > 0)
        {
            suspect = true;
            issues.Add("scrambled_equation: " + string.Join(", ", bad.Select(e => $"{e.Label} p{e.PdfPage}"))
                + " — stacked or garbled in the text layer (see the chunk's equations for each one's page box); "
                + "read them from the page image");
        }

        if (FlattenedTable(text) is { } caption)
        {
            issues.Add($"table_flattened: {caption} — the table's rows and columns are run together into one "
                + "stream of values; read it from the page image or extract it as a table");
        }

        if (hasMath || equations.Count > 0)
        {
            issues.Add("equations: contains maths flattened to a line — fractions, roots, exponents and indices lose "
                + "their structure (e.g. a/b·c vs a/(b·c)); verify any expression against the page image");
        }

        bool check = hasMath || equations.Count > 0 || issues.Count > 0;
        string quality = suspect ? "suspect" : check ? "check" : "ok";
        return (quality, issues);
    }

    // A token holding one letter three or more times in a row: "fccc", "M0EEEE", "Asss".
    [GeneratedRegex(@"[\p{L}\p{N},]*(\p{L})\1\1[\p{L}\p{N},]*")]
    private static partial Regex RepeatedLetterToken();

    // Roman numerals ("III", "xxx") and "www" are real tokens.
    [GeneratedRegex(@"^(?:[IVXLCDM]+|[ivxlcdm]+|www|WWW)$")]
    private static partial Regex RomanOrWww();

    internal static IReadOnlyList<string> RepeatedLetterTokens(string text) =>
        [.. RepeatedLetterToken().Matches(text).Select(m => m.Value).Where(t => !RomanOrWww().IsMatch(t))
            .Distinct(StringComparer.Ordinal).Take(8)];

    [GeneratedRegex(@"^[(]?[≤≥<>±+−–-]?\d+(?:[.,]\d+)*[%)]?$|^[≤≥<>–—-]$")]
    private static partial Regex TableValue();

    [GeneratedRegex(@"\bTable\s+[A-Z]?\.?\d+(?:\.\d+)*[a-z]?")]
    private static partial Regex TableCaption();

    /// <summary>A run of ≥ 12 consecutive bare values ("≤ 12 47 42 38 …") — a table read row by row
    /// into one line. Returns its caption ("Table 11.1", the last one named before the run) or
    /// "unnamed table"; null when there's no such run.</summary>
    internal static string? FlattenedTable(string text)
    {
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int run = 0, runStart = -1;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (TableValue().IsMatch(tokens[i]))
            {
                if (run++ == 0)
                {
                    runStart = i;
                }

                if (run >= 12)
                {
                    string before = string.Join(' ', tokens.Take(runStart));
                    Match last = TableCaption().Matches(before).LastOrDefault() ?? TableCaption().Match(text);
                    return last.Success ? last.Value : "unnamed table";
                }
            }
            else
            {
                run = 0;
            }
        }

        return null;
    }

    // Operators with nothing between them ("≥+", "= +", "+ ≤"), or an empty "()".
    [GeneratedRegex(@"[=+≤≥<>×·−]\s*[=+≤≥<>×·]|\(\s*\)")]
    private static partial Regex DanglingOperator();

    /// <summary>
    /// Why an extracted display equation can't be read linearly, or null. Stacked layouts (fractions,
    /// roots, stacked indices) come out of the text layer in drawing order: "v C k f k σ … Rd,c Rd,c
    /// … = (100 ) ρ + ≥+". Signals: parts on different baselines; operators with nothing to operate
    /// on or an empty "()"; a subscript token repeated (each symbol's subscript printed apart from
    /// it); or mostly 1–2 character tokens.
    /// </summary>
    internal static string? ScrambleReason(string raw, bool stacked)
    {
        if (stacked)
        {
            return "stacked layout (fraction, root or stacked index) flattened in drawing order";
        }

        if (DanglingOperator().IsMatch(raw))
        {
            return "operators with nothing to operate on — terms are out of order";
        }

        string[] tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool repeatedSubscript = tokens.Where(t => t.Length >= 3 && t.Contains(',') && t.Any(char.IsLetter))
            .GroupBy(t => t, StringComparer.Ordinal).Any(g => g.Count() > 1);
        bool fragments = tokens.Length >= 6 && tokens.Count(t => t.Length <= 2) >= tokens.Length * 0.6;
        return repeatedSubscript && fragments ? "subscripts printed apart from their symbols"
            : fragments && tokens.Length >= 10 ? "mostly one- and two-character fragments" : null;
    }
}
