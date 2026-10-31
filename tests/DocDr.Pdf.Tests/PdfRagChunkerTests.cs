using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DocDr.Pdf.Tests;

public sealed class PdfRagChunkerTests
{
    // A sentence ~70 chars — long enough that a few per page fills a small token window.
    private static string Body(string topic, int n) =>
        $"Paragraph {n} of the {topic} section explains the method and its assumptions in plain prose.";

    private static string[] Pages(int count, string topic) =>
        Enumerable.Range(1, count).Select(i => Body(topic, i)).ToArray();

    [Fact]
    public void Chunk_assigns_sections_and_page_ranges_from_the_bookmark_outline()
    {
        using var ws = new TempWorkspace();
        string[] pages = [.. Pages(4, "introduction"), .. Pages(4, "analysis"), .. Pages(4, "results")];
        string path = TestPdfBuilder.WritePdf(ws.Path("doc.pdf"), pages, bookmarks:
        [
            new TestPdfBuilder.Bookmark("Introduction", 0),
            new TestPdfBuilder.Bookmark("Analysis", 4),
            new TestPdfBuilder.Bookmark("Results", 8),
        ]);
        using var doc = PdfDocument.Load(path);

        RagChunkResult result = PdfRagChunker.Chunk(doc, new RagChunkOptions { TargetTokens = 60 });

        Assert.NotEmpty(result.Chunks);
        Assert.Equal(3, result.SectionCount);
        Assert.Equal(["Introduction", "Analysis", "Results"],
            result.Chunks.Select(c => c.SectionTitle).Distinct().ToArray());
        Assert.All(result.Chunks, c => Assert.InRange(c.PageStart, 0, c.PageEnd));
        Assert.All(result.Chunks.Where(c => c.SectionTitle == "Analysis"),
            c => Assert.InRange(c.PageStart, 4, 7));
        Assert.Equal(Enumerable.Range(0, result.Chunks.Count), result.Chunks.Select(c => c.ChunkIndex));
    }

    [Fact]
    public void Chunk_stays_within_the_token_budget()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("big.pdf"), Pages(40, "shear"));
        using var doc = PdfDocument.Load(path);

        var options = new RagChunkOptions { TargetTokens = 80 };
        RagChunkResult result = PdfRagChunker.Chunk(doc, options);

        Assert.True(result.Chunks.Count > 1);
        Assert.All(result.Chunks, c => Assert.True(c.Text.Length <= options.TargetTokens * 4));
    }

    [Fact]
    public void Chunk_overlap_goes_in_context_before_not_inline()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("ov.pdf"), Pages(30, "durability"));
        using var doc = PdfDocument.Load(path);

        var chunks = PdfRagChunker.Chunk(doc, new RagChunkOptions { TargetTokens = 70, IncludeSectionHeading = false }).Chunks;

        Assert.True(chunks.Count > 2);
        Assert.Equal(string.Empty, chunks[0].ContextBefore);
        for (int i = 1; i < chunks.Count; i++)
        {
            Assert.NotEmpty(chunks[i].ContextBefore);
            Assert.EndsWith(chunks[i].ContextBefore, chunks[i - 1].Text, StringComparison.Ordinal);
            Assert.False(chunks[i].Text.StartsWith(chunks[i].ContextBefore, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Chunk_strips_a_footer_that_repeats_on_every_page()
    {
        using var ws = new TempWorkspace();
        const string footer = "UNCONTROLLED COPY DO NOT DISTRIBUTE";
        string path = TestPdfBuilder.WritePdf(ws.Path("wm.pdf"), Pages(12, "detailing"), watermark: footer);
        using var doc = PdfDocument.Load(path);

        var chunks = PdfRagChunker.Chunk(doc, new RagChunkOptions { TargetTokens = 60 }).Chunks;

        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.DoesNotContain("UNCONTROLLED COPY", c.Text));
    }

    [Fact]
    public void Chunk_gives_each_chunk_a_stable_id_a_token_estimate_and_a_section_heading()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("hdr.pdf"), Pages(10, "anchorage"), bookmarks:
        [
            new TestPdfBuilder.Bookmark("Bond", 0),
            new TestPdfBuilder.Bookmark("Laps", 5),
        ]);
        using var doc = PdfDocument.Load(path);

        var chunks = PdfRagChunker.Chunk(doc, new RagChunkOptions { TargetTokens = 90 }).Chunks;

        Assert.Equal(chunks.Select((_, i) => $"hdr#c{i:D4}"), chunks.Select(c => c.Id));
        Assert.All(chunks, c => Assert.Equal(c.Text.Length / 4, c.TokenEstimate));
        Assert.All(chunks, c => Assert.StartsWith($"hdr — {c.SectionTitle}\n\n", c.Text));

        var noHeading = PdfRagChunker.Chunk(
            doc, new RagChunkOptions { TargetTokens = 90, IncludeSectionHeading = false }).Chunks;
        Assert.All(noHeading, c => Assert.DoesNotContain("—", c.Text[..Math.Min(40, c.Text.Length)]));
    }

    [Fact]
    public void Chunk_reports_pages_with_no_text_layer()
    {
        using var ws = new TempWorkspace();
        string[] pages = ["Real text on the first page describing the scope.", "", "More real text here."];
        string path = TestPdfBuilder.WritePdf(ws.Path("blank.pdf"), pages);
        using var doc = PdfDocument.Load(path);

        RagChunkResult result = PdfRagChunker.Chunk(doc);

        Assert.Equal([1], result.PagesWithoutText);
    }

    [Fact]
    public void WriteJsonl_emits_one_snake_case_object_per_line()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("j.pdf"), Pages(6, "materials"));
        using var doc = PdfDocument.Load(path);
        var chunks = PdfRagChunker.Chunk(doc, new RagChunkOptions { TargetTokens = 60 }).Chunks;

        string outPath = ws.Path("out.jsonl");
        PdfRagChunker.WriteJsonl(chunks, outPath);

        string[] lines = File.ReadAllLines(outPath);
        Assert.Equal(chunks.Count, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        JsonElement root = first.RootElement;
        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.TryGetProperty("text", out _));
        Assert.True(root.TryGetProperty("source_path", out _));
        Assert.True(root.TryGetProperty("page_start", out _));
        Assert.True(root.TryGetProperty("section_title", out _));
        Assert.True(root.TryGetProperty("token_estimate", out _));
        Assert.Equal("j.pdf", root.GetProperty("source_path").GetString());
        Assert.Equal(root.GetProperty("page_start").GetInt32() + 1, root.GetProperty("pdf_page_start").GetInt32());
        Assert.True(root.TryGetProperty("page_label_start", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("sections").ValueKind);
        Assert.True(root.TryGetProperty("context_before", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("equations").ValueKind);
        Assert.Equal(0, root.GetProperty("chunk_index").GetInt32());
        Assert.Equal("ok", root.GetProperty("quality").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("issues").ValueKind);
    }

    // --- Equation pages (the 8.94 pattern: a stacked ⅓ exponent, a lone variable, a footer page no.) ---

    private static string EquationPage(TempWorkspace ws, string name, params TestPdfBuilder.Run[] extra)
    {
        var runs = new List<TestPdfBuilder.Run>
        {
            new("The design punching shear stress resistance should be calculated as follows", 72, 700, 10),
            new("1", 330, 612, 6),   // ⅓ exponent: numerator…
            new("3", 330, 600, 6),   // …and denominator, each a line of its own
            new("d", 300, 560, 10),  // a variable alone under a fraction bar
            new("where the shear force per unit width may be averaged", 72, 520, 10),
            new("142", 72, 30, 10),  // the page number, in the footer band
        };
        runs.AddRange(extra);
        return TestPdfBuilder.WriteRuns(ws.Path(name), runs);
    }

    private static string[] Words(RagChunkResult r) =>
        string.Join(' ', r.Chunks.Select(c => c.Text)).Split(' ', '\n');

    [Fact]
    public void Lone_digits_and_letters_mid_page_are_kept_but_the_footer_page_number_is_dropped()
    {
        using var ws = new TempWorkspace();
        using var doc = PdfDocument.Load(EquationPage(ws, "eq.pdf"));

        string[] words = Words(PdfRagChunker.Chunk(doc, new RagChunkOptions { IncludeSectionHeading = false }));

        Assert.Contains("1", words);
        Assert.Contains("3", words);
        Assert.Contains("d", words);
        Assert.DoesNotContain("142", words);
    }

    [Fact]
    public void A_glyph_drawn_twice_at_the_same_spot_is_kept_once()
    {
        using var ws = new TempWorkspace();
        using var doc = PdfDocument.Load(EquationPage(ws, "dbl.pdf",
            new TestPdfBuilder.Run("Rdc", 200, 450, 10),
            new TestPdfBuilder.Run("Rdc", 200, 450, 10)));

        RagChunkResult result = PdfRagChunker.Chunk(doc, new RagChunkOptions { IncludeSectionHeading = false });
        string all = string.Join(' ', result.Chunks.Select(c => c.Text));

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(all, "R"));
        Assert.DoesNotContain("RR", all);
    }

    // --- Quality flags ----------------------------------------------------------------------

    private static PdfTextQuality.ScriptProfile LatinDoc() =>
        PdfTextQuality.ScriptProfile.Build(["The design shear resistance of the slab is given by the expression below."]);

    [Fact]
    public void Plain_text_is_ok()
    {
        (string q, IReadOnlyList<string> issues) = PdfTextQuality.Assess("Concrete cover to reinforcement.", false, LatinDoc());
        Assert.Equal("ok", q);
        Assert.Empty(issues);
    }

    [Fact]
    public void Unmapped_glyphs_and_foreign_script_symbols_make_a_chunk_suspect()
    {
        // The agent's report: "݇pb" (Syriac for k), "ଵ ଷ" (Oriya for 1/3), and replacement characters.
        (string q, IReadOnlyList<string> issues) = PdfTextQuality.Assess(
            "τRd,c = 0,6 γV ⋅ \u0747pb (100 ρl) \u0B35 \u0B37 \uFFFD", true, LatinDoc());

        Assert.Equal("suspect", q);
        Assert.Contains(issues, i => i.StartsWith("unmapped_glyphs: 1 ", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.StartsWith("unexpected_characters: 3 ", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.StartsWith("equations:", StringComparison.Ordinal));
    }

    [Fact]
    public void Maths_alone_asks_for_a_check_and_greek_symbols_are_not_suspect()
    {
        (string q, IReadOnlyList<string> issues) = PdfTextQuality.Assess("τRd,c ≤ 0,5 / γV · √fck", true, LatinDoc());
        Assert.Equal("check", q);
        Assert.Single(issues);
    }

    [Fact]
    public void A_document_written_in_another_script_is_not_flagged_for_it()
    {
        var hindi = PdfTextQuality.ScriptProfile.Build(["\u0915\u0902\u0915\u094D\u0930\u0940\u091F \u0915\u0935\u0930"]);
        Assert.Equal("ok", PdfTextQuality.Assess("\u0915\u0935\u0930 25 mm", false, hindi).Quality);
    }

    [Fact]
    public void Maths_letters_are_normalised_to_plain_ones()
    {
        Assert.Equal("τ", PdfTextQuality.NormaliseMathLetter(0x1D70F)); // 𝜏 MATHEMATICAL ITALIC SMALL TAU
        Assert.Equal("c", PdfTextQuality.NormaliseMathLetter(0x1D450)); // 𝑐
        Assert.Equal("h", PdfTextQuality.NormaliseMathLetter(0x210E));  // ℎ
        Assert.Equal("²", PdfTextQuality.NormaliseMathLetter(0x00B2));  // untouched outside the maths block
        Assert.True(PdfTextQuality.IsMathCodePoint(0x1D70F));
        Assert.True(PdfTextQuality.IsMathCodePoint(0x22C5));             // ⋅
        Assert.False(PdfTextQuality.IsMathCodePoint('x'));
    }

    // --- Char-level line building (real PDFium char sequences from BS EN 1992-1-1 (8.94)) ---

    private static PdfChunkText.RawChar C(int unit, double x, double y = 400) =>
        new(unit, new PdfRect(x, y + 8, x + 5, y));

    private static IEnumerable<PdfChunkText.RawChar> Text(string s, double x, double y = 400) =>
        s.Select((ch, k) => C(ch, x + (k * 6), y));

    [Fact]
    public void A_maths_glyph_mapped_to_a_doubled_surrogate_pair_comes_out_once_as_a_plain_letter()
    {
        // Word's ToUnicode maps glyph 0747 to "𝑘𝑘" (D835 DC58 D835 DC58): four PDFium chars, one box.
        var chars = new List<PdfChunkText.RawChar>
        {
            C(0xD835, 100), C(0xDC58, 100), C(0xD835, 100), C(0xDC58, 100),
        };
        chars.AddRange(Text("pb", 106));

        ChunkPage page = PdfChunkText.BuildPage(chars, bottom: 0, top: 792);

        Assert.Equal("kpb", page.Lines.Single().Text);
        Assert.True(page.Lines.Single().HasMath);
        Assert.Equal(1, page.DoubledGlyphsRemoved);
    }

    [Fact]
    public void A_ligature_split_into_repeated_ascii_letters_is_left_alone()
    {
        // An "ff" ligature glyph expands to two 'f' chars sharing one box: "effect" must stay intact.
        var chars = new List<PdfChunkText.RawChar> { C('e', 100), C('f', 106), C('f', 106) };
        chars.AddRange(Text("ect", 112));

        Assert.Equal("effect", PdfChunkText.BuildPage(chars, 0, 792).Lines.Single().Text);
    }

    [Fact]
    public void A_lone_surrogate_becomes_a_replacement_character_not_an_exception()
    {
        ChunkPage page = PdfChunkText.BuildPage([C(0xD835, 100), C('x', 106)], 0, 792);
        Assert.Equal("\uFFFDx", page.Lines.Single().Text);
    }

    [Fact]
    public void Lines_split_on_newlines_and_only_header_or_footer_lines_count_as_margin()
    {
        var chars = new List<PdfChunkText.RawChar>();
        chars.AddRange(Text("3", 300, 600));      // mid-page exponent
        chars.Add(C('\r', 0)); chars.Add(C('\n', 0));
        chars.AddRange(Text("142", 72, 30));      // footer page number

        ChunkPage page = PdfChunkText.BuildPage(chars, 0, 792);

        Assert.Equal(["3", "142"], page.Lines.Select(l => l.Text).ToArray());
        Assert.False(page.Lines[0].InMargin);
        Assert.True(page.Lines[1].InMargin);
    }
}
