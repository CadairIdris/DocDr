using System.Collections.Generic;
using System.Linq;

namespace DocDr.Pdf.Tests;

/// <summary>
/// Fixes from an independent review of the chunk export against page images (duplicated subscript
/// glyphs, scrambled stacked equations, Symbol private-use pieces, leaking licence lines, mid-page
/// section titles, flattened tables, placeholder titles) and a textbook run (bogus "1.35 Gk"
/// headings, overlap repeated inline, the title in the running header).
/// </summary>
public sealed class PdfRagChunkerReviewTests
{
    private static PdfChunkText.RawChar C(int unit, double x, double y = 400, double h = 8, double w = 5) =>
        new(unit, new PdfRect(x, y + h, x + w, y));

    private static IEnumerable<PdfChunkText.RawChar> Text(string s, double x, double y = 400) =>
        s.Select((ch, k) => C(ch, x + (k * 6), y));

    private static IEnumerable<PdfChunkText.RawChar> NewLine() => [C('\r', 0), C('\n', 0)];

    private static string Line(IEnumerable<PdfChunkText.RawChar> chars) =>
        PdfChunkText.BuildPage([.. chars], 0, 792).Lines.Single().Text;

    private static PdfTextQuality.ScriptProfile LatinDoc() =>
        PdfTextQuality.ScriptProfile.Build(["The design shear resistance of the slab is given by the expression below."]);

    // --- Glyph repairs -----------------------------------------------------------------------

    [Fact]
    public void Overprinted_ascii_is_kept_once_but_an_ff_ligature_survives()
    {
        // Fake bold: "Rdc" drawn twice at the same spot.
        Assert.Equal("Rdc", Line([.. Text("Rdc", 100), .. Text("Rdc", 100)]));
        // An "ffi" ligature: two f's and an i at one box — the second f is real, a third isn't.
        Assert.Equal("effi", Line([C('e', 94), C('f', 100), C('f', 100), C('i', 100)]));
        Assert.Equal("ff", Line([C('f', 100), C('f', 100), C('f', 100)]));
    }

    [Fact]
    public void Glyphs_with_zero_height_boxes_at_the_same_spot_are_still_one_glyph()
    {
        // Some fonts report a degenerate box; a doubled 𝑘 (U+1D458) there must still collapse.
        Assert.Equal("kpb", Line([C(0xD835, 100, h: 0), C(0xDC58, 100, h: 0), C(0xD835, 100, h: 0), C(0xDC58, 100, h: 0),
            .. Text("pb", 106)]));
    }

    [Fact]
    public void Symbol_font_bracket_and_bar_pieces_get_their_meaning()
    {
        // Left paren top / extender / bottom, "a+b", right paren pieces; then a three-piece fraction bar.
        var chars = new List<PdfChunkText.RawChar>
        {
            C(0xF8EB, 100, 410), C(0xF8EC, 100, 400), C(0xF8ED, 100, 390),
            C('a', 106), C('+', 112), C('b', 118),
            C(0xF8F6, 124, 410), C(0xF8F7, 124, 400), C(0xF8F8, 124, 390),
            C(' ', 130), C(0xF8E7, 136), C(0xF8E7, 141), C(0xF8E7, 146),
        };

        ChunkLine line = PdfChunkText.BuildPage(chars, 0, 792).Lines.Single();
        Assert.Equal("(a+b) /", line.Text);
        Assert.True(line.HasMath);
    }

    [Fact]
    public void Unmapped_hyphen_and_unicode_hyphens_become_plain_hyphens()
    {
        Assert.Equal("y-direction", Line([.. Text("y", 100), C(0x02, 106), .. Text("direction", 112)]));
        Assert.Equal("1992-1-1", Line([.. Text("1992", 100), C(0x2011, 124), C('1', 130), C(0x2011, 136), C('1', 142)]));
        // Not between letters: still a control character, counted as unmapped.
        Assert.Equal("2\u00023", Line([C('2', 100), C(0x02, 106), C('3', 112)]));
        Assert.Equal(1, PdfTextQuality.Count("2\u00023", LatinDoc()).Unmapped);
    }

    // --- Equation location -------------------------------------------------------------------

    [Fact]
    public void A_stacked_display_equation_is_located_and_marked_stacked()
    {
        // (O.7): "NB = π²EI" over "l0²", label at the right margin, as PDFium emits it (three lines).
        var chars = new List<PdfChunkText.RawChar>();
        chars.AddRange(Text("NB = π2EI", 90, 158));
        chars.AddRange(NewLine());
        chars.AddRange(Text("l0", 126, 140));
        chars.AddRange(NewLine());
        chars.AddRange(Text("2 (O.7)", 520, 149));

        ChunkPage page = PdfChunkText.BuildPage(chars, 0, 792, left: 0, right: 612);

        ChunkEquation eq = Assert.Single(page.Equations);
        Assert.Equal("(O.7)", eq.Label);
        Assert.True(eq.Stacked);
        Assert.StartsWith("NB = π2EI", eq.RawText, StringComparison.Ordinal);
        Assert.InRange(eq.Bounds.Left, 89, 91);
        Assert.InRange(eq.Bounds.Bottom, 139, 141);
        Assert.InRange(eq.Bounds.Top, 165, 167);
    }

    [Fact]
    public void A_one_line_equation_is_not_stacked_and_a_reference_in_prose_is_not_an_equation()
    {
        var chars = new List<PdfChunkText.RawChar>();
        chars.AddRange(Text("FVB = FVBB", 90, 300));
        chars.AddRange(Text("(O.1)", 520, 300));
        chars.AddRange(NewLine());
        chars.AddRange(Text("the resistance should be checked using Expression (8.94)", 90, 250));
        chars.AddRange(NewLine());
        chars.AddRange(Text("(3.1)", 100, 200)); // a label at the left margin: not a display equation

        ChunkPage page = PdfChunkText.BuildPage(chars, 0, 792, left: 0, right: 612);

        ChunkEquation eq = Assert.Single(page.Equations);
        Assert.Equal("(O.1)", eq.Label);
        Assert.False(eq.Stacked);
        Assert.Equal("FVB = FVBB", eq.RawText);
    }

    [Theory]
    [InlineData("v C k f k σ (vk σ ) 1/ 3 Rd,c Rd,c l ck 1 cp min 1 cp = (100 ) ρ + ≥+")] // (6.47), 2004
    [InlineData("k 2,0 in mm 200 1 d d = + ≤")]                                            // k = 1 + √(200/d)
    [InlineData("k = 2,0 200 1+ ≤ d")]
    [InlineData("() k η kη η f σ 1 2 2 cm c + − − =")]                                     // (3.14)
    public void Scrambled_equations_from_the_review_are_recognised(string raw)
    {
        Assert.NotNull(PdfTextQuality.ScrambleReason(raw, stacked: false));
    }

    [Theory]
    [InlineData("τRd,c = 0,66 γV ⋅ (100 ρl ⋅ fck ⋅ ddg / d)1/3")]
    [InlineData("FVBS = kc ⋅ Gcd ⋅ Ac")]
    [InlineData("Cm = 0,6 + 0,4rm ≥ 0,4")]
    public void Equations_in_reading_order_are_not_called_scrambled(string raw)
    {
        Assert.Null(PdfTextQuality.ScrambleReason(raw, stacked: false));
        Assert.NotNull(PdfTextQuality.ScrambleReason(raw, stacked: true));
    }

    // --- Chunk quality -----------------------------------------------------------------------

    [Fact]
    public void Letters_repeated_by_a_broken_subscript_map_make_a_chunk_suspect()
    {
        (string q, IReadOnlyList<string> issues) = PdfTextQuality.Assess(
            "The cracking moment Mccc uses fccc and Asss; see clause III at www.bsi.", false, LatinDoc());

        Assert.Equal("suspect", q);
        string issue = Assert.Single(issues, i => i.StartsWith("duplicated_glyphs:", StringComparison.Ordinal));
        Assert.Contains("Mccc", issue);
        Assert.Contains("fccc", issue);
        Assert.DoesNotContain("III", issue);
        Assert.DoesNotContain("www", issue);
    }

    [Fact]
    public void A_scrambled_equation_makes_its_chunk_suspect_and_names_it()
    {
        var eq = new RagEquation("(6.47)", 106, 107, [1, 2, 3, 4], "x", "suspect", ["scrambled: …"]);
        (string q, IReadOnlyList<string> issues) = PdfTextQuality.Assess("Shear (6.47) follows.", true, LatinDoc(), [eq]);

        Assert.Equal("suspect", q);
        Assert.Contains(issues, i => i.StartsWith("scrambled_equation: (6.47) p107", StringComparison.Ordinal));
    }

    [Fact]
    public void A_table_read_into_a_number_stream_is_flagged_with_its_caption()
    {
        const string text = "Basic anchorage lengths are given in Table 11.1 Values of lbd for bars "
            + "≤ 12 47 42 38 36 33 31 30 27 14 50 44 40 and so on.";
        Assert.Equal("Table 11.1", PdfTextQuality.FlattenedTable(text));
        (string q, IReadOnlyList<string> issues) = PdfTextQuality.Assess(text, false, LatinDoc());
        Assert.Equal("check", q);
        Assert.Contains(issues, i => i.StartsWith("table_flattened: Table 11.1", StringComparison.Ordinal));

        Assert.Null(PdfTextQuality.FlattenedTable("Use 25 mm bars at 150 mm centres with 40 mm cover."));
    }

    [Theory]
    [InlineData("BS EN 1992‑1‑1:2023 Eurocode 2 for buildings.", "BS EN 1992-1-1:2023 Eurocode 2 for buildings")]
    [InlineData("Microsoft Word - Guide final.docx", "Guide final")]
    public void Titles_are_cleaned(string raw, string expected)
    {
        Assert.Equal(expected, PdfRagChunker.CleanTitle(raw));
    }

    // --- Whole documents ---------------------------------------------------------------------

    private static string Para(string topic, int n) =>
        $"Paragraph {n} on {topic} sets out the method and its assumptions in plain prose here.";

    [Fact]
    public void A_licence_stamp_is_stripped_even_on_a_page_of_its_own()
    {
        using var ws = new TempWorkspace();
        string[] pages = [.. Enumerable.Range(1, 5).Select(i => Para("cover", i)),
            "This page deliberately left blank\nLicensed copy: Joe Bloggs, Acme Ltd, Version correct as of 01/01/2026"];
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("lic.pdf"), pages));

        var chunks = PdfRagChunker.Chunk(doc, new RagChunkOptions { IncludeSectionHeading = false }).Chunks;

        Assert.Contains(chunks, c => c.Text.Contains("deliberately left blank", StringComparison.Ordinal));
        Assert.All(chunks, c => Assert.DoesNotContain("Joe Bloggs", c.Text));
    }

    [Fact]
    public void Text_continuing_a_section_at_the_top_of_a_page_keeps_that_sections_title()
    {
        using var ws = new TempWorkspace();
        string[] pages =
        [
            "1 Scope\n" + Para("scope", 1),
            Para("scope", 2) + "\n2 Materials\n" + Para("materials", 1),
            Para("materials", 2),
        ];
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("sec.pdf"), pages, bookmarks:
        [
            new TestPdfBuilder.Bookmark("1 Scope", 0),
            new TestPdfBuilder.Bookmark("2 Materials", 1),
            new TestPdfBuilder.Bookmark("3 Durability", 2),
        ]));

        var chunks = PdfRagChunker.Chunk(doc,
            new RagChunkOptions { TargetTokens = 600, MinChunkTokens = 1, IncludeSectionHeading = false }).Chunks;

        RagChunk scope = Assert.Single(chunks, c => c.Text.Contains("Paragraph 2 on scope", StringComparison.Ordinal));
        Assert.Equal("1 Scope", scope.SectionTitle);
        Assert.DoesNotContain("materials", scope.Text); // a new section starts a new chunk
        RagChunk materials = Assert.Single(chunks, c => c.Text.StartsWith("2 Materials", StringComparison.Ordinal));
        Assert.Equal("2 Materials", materials.SectionTitle);
        Assert.Equal(2, materials.PdfPageStart);
        Assert.Equal(string.Empty, materials.ContextBefore); // no overlap across a section start
    }

    [Fact]
    public void Bookmarks_beat_numbered_values_mistaken_for_headings()
    {
        using var ws = new TempWorkspace();
        string[] pages = [.. Enumerable.Range(1, 6).Select(i => $"1.35 Gk\n{i}.1 Loading\n{Para("loads", i)}")];
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("gk.pdf"), pages, bookmarks:
        [
            new TestPdfBuilder.Bookmark("Chapter 1 Introduction", 0),
            new TestPdfBuilder.Bookmark("Chapter 2 Actions", 2),
            new TestPdfBuilder.Bookmark("Chapter 3 Analysis", 4),
        ]));

        var chunks = PdfRagChunker.Chunk(doc, new RagChunkOptions { TargetTokens = 60 }).Chunks;

        Assert.All(chunks, c => Assert.DoesNotContain("Gk", c.SectionTitle));
        Assert.Contains(chunks, c => c.SectionTitle == "Chapter 3 Analysis");
    }

    [Fact]
    public void Numbered_values_are_not_headings_without_bookmarks_either()
    {
        using var ws = new TempWorkspace();
        string[] pages = [.. Enumerable.Range(1, 6).Select(i => $"1.35 Gk\n{Para("loads", i)}")];
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("gk2.pdf"), pages));

        var chunks = PdfRagChunker.Chunk(doc, new RagChunkOptions { TargetTokens = 60 }).Chunks;

        Assert.All(chunks, c => Assert.DoesNotContain("Gk", c.SectionTitle));
    }

    [Fact]
    public void A_placeholder_title_gives_way_to_the_running_header()
    {
        using var ws = new TempWorkspace();
        string[] pages = [.. Enumerable.Range(1, 8).Select(i => Para("bending", i))];
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("30187350.pdf"), pages,
            info: new TestPdfBuilder.PdfInfo(Title: "30187350.pdf"),
            header: "Designers Guide to EN 1992-1-1 #"));

        var chunks = PdfRagChunker.Chunk(doc, new RagChunkOptions { IncludeSectionHeading = false }).Chunks;

        Assert.All(chunks, c => Assert.Equal("Designers Guide to EN 1992-1-1", c.DocTitle));
        Assert.All(chunks, c => Assert.DoesNotContain("Designers Guide", c.Text)); // and it's stripped as chrome
        Assert.All(chunks, c => Assert.Equal("30187350.pdf", c.SourcePath));
    }
}
