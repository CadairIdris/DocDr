using System.Linq;
using DocDr.Pdf;

namespace DocDr.Pdf.Tests;

public sealed class PdfMergeTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "docdr-merge-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string name, params string[] pages) =>
        TestPdfBuilder.WritePdf(Path.Combine(_dir, name), pages);

    [Fact]
    public void Merge_concatenates_pages_in_order()
    {
        string a = Write("a.pdf", "A one", "A two");
        string b = Write("b.pdf", "B one");
        string c = Write("c.pdf", "C one", "C two", "C three");

        MergeResult result = PdfDocument.Merge([a, b, c]);
        using PdfDocument doc = result.Document;

        Assert.Equal(6, doc.PageCount);
        Assert.Null(doc.FilePath);
        Assert.True(doc.IsDirty);
        Assert.Equal(new[] { 0, 2, 3 }, result.SourceStartPages);
        Assert.Contains("A one", PdfTextExtractor.GetPageText(doc, 0));
        Assert.Contains("B one", PdfTextExtractor.GetPageText(doc, 2));
        Assert.Contains("C three", PdfTextExtractor.GetPageText(doc, 5));
    }

    [Fact]
    public void Merged_document_survives_a_structural_edit_and_save()
    {
        using PdfDocument doc = PdfDocument.Merge(
            [Write("a.pdf", "A1", "A2"), Write("b.pdf", "B1")]).Document;

        doc.RotatePages([0], PdfRotation.Clockwise90);
        byte[] saved = doc.SaveToBytes();

        using PdfDocument reloaded = PdfDocument.Load(saved);
        Assert.Equal(3, reloaded.PageCount);
        Assert.Contains("B1", PdfTextExtractor.GetPageText(reloaded, 2));
    }

    [Fact]
    public void SetOutline_round_trips_through_save_and_reload()
    {
        using PdfDocument doc = PdfDocument.Merge(
            [Write("a.pdf", "A1", "A2"), Write("b.pdf", "B1", "B2")]).Document;

        var outline = new[]
        {
            new PdfBookmark("Chapter A", 0, new[] { new PdfBookmark("A.1 Intro", 1, []) }),
            new PdfBookmark("Chapter B", 2, []),
        };
        doc.SetOutline(outline);

        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());
        IReadOnlyList<PdfBookmark> read = PdfBookmarks.Read(reloaded);

        Assert.Equal(2, read.Count);
        Assert.Equal("Chapter A", read[0].Title);
        Assert.Equal(0, read[0].PageIndex);
        Assert.Single(read[0].Children);
        Assert.Equal("A.1 Intro", read[0].Children[0].Title);
        Assert.Equal(1, read[0].Children[0].PageIndex);
        Assert.Equal("Chapter B", read[1].Title);
        Assert.Equal(2, read[1].PageIndex);
    }

    [Fact]
    public void GetOutline_prefers_a_generated_outline_over_the_file_one()
    {
        string path = TestPdfBuilder.WritePdf(
            Path.Combine(_dir, "own.pdf"), ["p1", "p2"],
            bookmarks: [new TestPdfBuilder.Bookmark("File bookmark", 0)]);
        using PdfDocument doc = PdfDocument.Load(path);

        Assert.Equal("File bookmark", doc.GetOutline()[0].Title);

        doc.SetOutline([new PdfBookmark("Generated", 1, [])]);
        Assert.True(doc.HasGeneratedOutline);
        Assert.Equal("Generated", doc.GetOutline()[0].Title);
    }

    [Fact]
    public void PdfHeadings_SubHeadings_keeps_numbered_headings_only()
    {
        string path = Write("ch.pdf",
            "5 Analysis of stress and strain",
            "5.1 Introduction",
            "5.2 Mohr's circle of stress");
        using PdfDocument doc = PdfDocument.Load(path);

        IReadOnlyList<PdfBookmark> subs = PdfHeadings.SubHeadings(doc);

        Assert.All(subs, s => Assert.Contains(".", s.Title.Split(' ')[0]));
        Assert.Contains(subs, s => s.Title.StartsWith("5.1"));
        Assert.DoesNotContain(subs, s => s.Title.StartsWith("5 "));
    }

    [Fact]
    public void PdfHeadings_Shift_offsets_every_page_index()
    {
        var tree = new[]
        {
            new PdfBookmark("A", 0, new[] { new PdfBookmark("A.1", 2, []) }),
        };

        IReadOnlyList<PdfBookmark> shifted = PdfHeadings.Shift(tree, 10);

        Assert.Equal(10, shifted[0].PageIndex);
        Assert.Equal(12, shifted[0].Children[0].PageIndex);
    }

    [Fact]
    public void Generated_outline_is_not_doubled_on_a_second_save()
    {
        using PdfDocument doc = PdfDocument.Merge([Write("a.pdf", "A1"), Write("b.pdf", "B1")]).Document;
        doc.SetOutline([new PdfBookmark("A", 0, []), new PdfBookmark("B", 1, [])]);

        doc.SaveToBytes();
        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());

        Assert.Equal(2, PdfBookmarks.Read(reloaded).Count);
    }
}
