namespace DocDr.Pdf.Tests;

public sealed class PdfExtractTests
{
    private static PdfDocument Five(TempWorkspace ws, IReadOnlyList<TestPdfBuilder.Bookmark>? bookmarks = null) =>
        PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("five.pdf"),
            ["page one", "page two", "page three", "page four", "page five"], bookmarks: bookmarks));

    [Fact]
    public void Extract_writes_just_the_chosen_pages_in_order()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Five(ws);

        using PdfDocument extract = PdfDocument.Load(doc.ExtractPages([3, 1, 3]));

        Assert.Equal(2, extract.PageCount);
        Assert.Contains("page two", PdfTextExtractor.GetPageText(extract, 0));
        Assert.Contains("page four", PdfTextExtractor.GetPageText(extract, 1));
        Assert.Equal(5, doc.PageCount); // the source is untouched
    }

    [Fact]
    public void Annotations_come_along_only_when_asked_and_the_source_keeps_its_own()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Five(ws);
        doc.AddAnnotation(3, PdfAnnotation.NewComment(new PdfRect(72, 700, 90, 682), "check this", "rob"));

        using (PdfDocument with = PdfDocument.Load(doc.ExtractPages([3])))
        {
            Assert.Equal("check this", Assert.Single(with.GetAnnotations(0)).Contents);
        }

        using (PdfDocument without = PdfDocument.Load(doc.ExtractPages([3], new PdfExtractOptions { IncludeAnnotations = false })))
        {
            Assert.Empty(without.GetAnnotations(0));
        }

        Assert.Single(doc.GetAnnotations(3));
        Assert.NotEmpty(doc.ExtractPages([3])); // baking was undone: extracting again still works
    }

    [Fact]
    public void Bookmarks_inside_the_range_are_retargeted_and_the_title_is_set()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Five(ws,
            [new("Chapter 1", 0), new("Chapter 2", 2), new("Chapter 3", 4)]);

        using PdfDocument extract = PdfDocument.Load(doc.ExtractPages([2, 3],
            new PdfExtractOptions { Title = "Chapter 2 only" }));

        PdfBookmark only = Assert.Single(extract.GetOutline());
        Assert.Equal("Chapter 2", only.Title);
        Assert.Equal(0, only.PageIndex);
        Assert.Equal("Chapter 2 only", extract.Info.Title);
    }

    [Fact]
    public void A_bookmark_outside_the_range_gives_way_to_its_children_inside_it()
    {
        IReadOnlyList<PdfBookmark> tree =
        [
            new("8 Ultimate limit states", 0,
            [
                new("8.4 Punching", 5, [new("8.4.3 Without shear reinforcement", 6, [])]),
            ]),
        ];

        var sliced = PdfDocument.SliceOutline(tree, new Dictionary<int, int> { [5] = 0, [6] = 1 });

        PdfBookmark punching = Assert.Single(sliced);
        Assert.Equal("8.4 Punching", punching.Title);
        Assert.Equal(1, Assert.Single(punching.Children).PageIndex);
    }

    [Fact]
    public void Nothing_to_extract_is_an_error()
    {
        using var ws = new TempWorkspace();
        using PdfDocument doc = Five(ws);
        Assert.Throws<ArgumentException>(() => doc.ExtractPages([]));
        Assert.ThrowsAny<Exception>(() => doc.ExtractPages([9]));
    }

    [Theory]
    [InlineData("3, 7-9, 12", new[] { 2, 6, 7, 8, 11 })]
    [InlineData("9-7", new[] { 6, 7, 8 })]
    [InlineData("150 – 152", new[] { 149, 150, 151 })]
    [InlineData("4;4", new[] { 3 })]
    public void Page_ranges_parse_as_people_type_them(string text, int[] expected)
    {
        Assert.True(PdfPageRange.TryParse(text, 200, out IReadOnlyList<int> pages, out _));
        Assert.Equal(expected, pages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("201")]
    [InlineData("abc")]
    [InlineData("3-")]
    public void Bad_page_ranges_are_explained(string text)
    {
        Assert.False(PdfPageRange.TryParse(text, 200, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Ranges_format_compactly_one_based() =>
        Assert.Equal("1,3-5,8", PdfPageRange.Format([0, 2, 3, 4, 7, 4]));
}
