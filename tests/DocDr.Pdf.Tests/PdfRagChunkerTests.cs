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
    public void Chunk_overlaps_adjacent_windows()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("ov.pdf"), Pages(30, "durability"));
        using var doc = PdfDocument.Load(path);

        var chunks = PdfRagChunker.Chunk(doc, new RagChunkOptions { TargetTokens = 70 }).Chunks;

        Assert.True(chunks.Count > 2);
        for (int i = 1; i < chunks.Count; i++)
        {
            string prevTail = chunks[i - 1].Text[^20..];
            Assert.Contains(prevTail[..12], chunks[i].Text);
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
        Assert.Equal(0, root.GetProperty("chunk_index").GetInt32());
    }
}
