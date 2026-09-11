namespace DocDr.Pdf.Tests;

/// <summary>
/// Exercises the clause-scan cache through <see cref="PdfDocument"/>'s public surface only —
/// <c>PdfClauseCache</c> itself is internal, tested the same way <c>PdfOutlineWriter</c> and
/// <c>PdfMetadataWriter</c> are: via the document round-trip, not direct calls.
/// </summary>
public sealed class PdfClauseCacheTests
{
    private static PdfCodeStructure SampleStructure() => new(
        Clauses:
        [
            new PdfClause
            {
                Number = "6",
                Title = "Structural analysis",
                Level = 1,
                PageIndex = 3,
                Children =
                [
                    new PdfClause { Number = "6.1", Title = "General", Level = 2, PageIndex = 3 },
                    new PdfClause { Number = "6.4", Title = "Connections", Level = 2, PageIndex = 5 },
                ],
            },
            new PdfClause { Number = "A", Title = string.Empty, Level = 1, PageIndex = 40 },
        ],
        Captions: new Dictionary<string, int>
        {
            ["Figure 8.5"] = 12,
            ["Table 4.3"] = 20,
        });

    [Fact]
    public void A_plain_document_has_no_cached_clause_structure()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("plain.pdf"), ["hello"]);
        using var doc = PdfDocument.Load(path);

        Assert.Null(doc.CachedClauseStructure);
    }

    [Fact]
    public void SetClauseStructure_then_save_and_reopen_round_trips_the_structure()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("s.pdf"), ["one", "two", "three"]);

        byte[] saved;
        using (var doc = PdfDocument.Load(path))
        {
            doc.SetClauseStructure(SampleStructure());
            saved = doc.SaveToBytes();
        }

        File.WriteAllBytes(path, saved);
        using var reopened = PdfDocument.Load(path);

        PdfCodeStructure? read = reopened.CachedClauseStructure;
        Assert.NotNull(read);
        Assert.Equal(2, read!.Clauses.Count);
        Assert.Equal("6", read.Clauses[0].Number);
        Assert.Equal("Structural analysis", read.Clauses[0].Title);
        Assert.Equal(2, read.Clauses[0].Children.Count);
        Assert.Equal("6.4", read.Clauses[0].Children[1].Number);
        Assert.Equal(5, read.Clauses[0].Children[1].PageIndex);
        Assert.Equal("A", read.Clauses[1].Number);
        Assert.Equal(2, read.Captions.Count);
        Assert.Equal(12, read.Captions["Figure 8.5"]);

        // The cache must not have corrupted the file for PDFium's own reader.
        Assert.Equal(3, reopened.PageCount);
        Assert.Contains("two", PdfTextExtractor.GetPageText(reopened, 1));
    }

    [Fact]
    public void SetClauseStructure_marks_the_document_dirty_but_loading_a_cached_file_does_not()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("s.pdf"), ["one"]);

        byte[] saved;
        using (var doc = PdfDocument.Load(path))
        {
            Assert.False(doc.IsDirty);
            doc.SetClauseStructure(SampleStructure());
            Assert.True(doc.IsDirty);
            saved = doc.SaveToBytes();
        }

        File.WriteAllBytes(path, saved);
        using var reopened = PdfDocument.Load(path);
        Assert.False(reopened.IsDirty);
        Assert.NotNull(reopened.CachedClauseStructure);
    }

    [Fact]
    public void An_empty_structure_is_not_persisted()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("s.pdf"), ["one"]);

        byte[] saved;
        using (var doc = PdfDocument.Load(path))
        {
            doc.SetClauseStructure(PdfCodeStructure.Empty);
            saved = doc.SaveToBytes();
        }

        File.WriteAllBytes(path, saved);
        using var reopened = PdfDocument.Load(path);
        Assert.Null(reopened.CachedClauseStructure);
    }

    [Fact]
    public void The_cache_survives_alongside_a_generated_outline()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("s.pdf"), ["one", "two", "three"]);

        byte[] saved;
        using (var doc = PdfDocument.Load(path))
        {
            doc.SetOutline([new PdfBookmark("Chapter one", 0, [])]);
            doc.SetClauseStructure(SampleStructure());
            saved = doc.SaveToBytes();
        }

        File.WriteAllBytes(path, saved);
        using var reopened = PdfDocument.Load(path);

        Assert.NotNull(reopened.CachedClauseStructure);
        Assert.Single(reopened.GetOutline());
        Assert.Equal("Chapter one", reopened.GetOutline()[0].Title);
    }
}
