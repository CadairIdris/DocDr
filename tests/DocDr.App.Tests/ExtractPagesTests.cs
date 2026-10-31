using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

public sealed class ExtractPagesTests
{
    private static List<BookmarkNodeViewModel> Tree(params PdfBookmark[] roots) =>
        roots.Select(b => new BookmarkNodeViewModel(b, 0, i => (i + 1).ToString())).ToList();

    private static readonly List<BookmarkNodeViewModel> Book = Tree(
        new PdfBookmark("1 General", 0,
        [
            new PdfBookmark("1.1 Scope", 1, []),
            new PdfBookmark("1.2 Normative references", 3, []),
        ]),
        new PdfBookmark("2 Basis of design", 6, []),
        new PdfBookmark("Figure 2.1", 6, []),        // starts on the same page — not a boundary
        new PdfBookmark("3 Materials", 9, []));

    private static (int, int)? Range(string title)
    {
        BookmarkNodeViewModel node = Flatten(Book).Single(n => n.Title == title);
        return BookmarksViewModel.ChapterRange(Book, node, pageCount: 12);
    }

    private static IEnumerable<BookmarkNodeViewModel> Flatten(IEnumerable<BookmarkNodeViewModel> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));

    [Fact]
    public void A_chapter_runs_to_the_page_before_the_next_chapter_including_its_sections() =>
        Assert.Equal((0, 5), Range("1 General"));

    [Fact]
    public void A_section_ends_where_the_next_section_starts() =>
        Assert.Equal((1, 2), Range("1.1 Scope"));

    [Fact]
    public void A_bookmark_on_the_same_page_is_not_a_boundary() =>
        Assert.Equal((6, 8), Range("2 Basis of design"));

    [Fact]
    public void The_last_chapter_runs_to_the_end() =>
        Assert.Equal((9, 11), Range("3 Materials"));

    [Fact]
    public void The_dialog_prefills_one_based_pages_and_validates_edits()
    {
        var vm = new ExtractPagesViewModel([141], pageCount: 408, "BS EN 1992-1-1", hasAnnotations: false);
        Assert.Equal("142", vm.RangeText);
        Assert.Equal("1 page", vm.Summary);
        Assert.True(vm.ExtractCommand.CanExecute(null));
        Assert.False(vm.IncludeAnnotations); // nothing to include

        vm.RangeText = "150-152, 160";
        Assert.Equal("4 pages", vm.Summary);
        Assert.Equal(new[] { 149, 150, 151, 159 }, vm.Pages);

        vm.RangeText = "500";
        Assert.False(vm.ExtractCommand.CanExecute(null));
        Assert.NotEmpty(vm.Error);
    }

    [Fact]
    public void A_chapter_range_prefills_as_a_compact_range()
    {
        var vm = new ExtractPagesViewModel([5, 6, 7, 8], pageCount: 20, "x", hasAnnotations: true);
        Assert.Equal("6-9", vm.RangeText);
        Assert.True(vm.IncludeAnnotations);
    }
}
