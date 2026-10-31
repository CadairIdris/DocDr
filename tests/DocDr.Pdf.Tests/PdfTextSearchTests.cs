namespace DocDr.Pdf.Tests;

/// <summary>
/// The cached-text search path: <see cref="PdfTextMatcher"/> (pure), <see cref="PdfPageTextCache"/>
/// (version-keyed), and <see cref="PdfSearch"/> using both — warm searches only open matching
/// pages, results stream from the reader's page, and an edit never serves stale text.
/// </summary>
public sealed class PdfTextSearchTests
{
    // --- PdfTextMatcher ---------------------------------------------------------------------

    [Fact]
    public void Matcher_finds_a_phrase_across_a_line_break_and_maps_back_to_char_indices()
    {
        const string text = "design of\r\nconcrete structures";
        var hits = PdfTextMatcher.Find(text, "of concrete", PdfSearchOptions.None);

        (int start, int count) = Assert.Single(hits);
        Assert.Equal(text.IndexOf("of", StringComparison.Ordinal), start);
        Assert.Equal("of\r\nconcrete", text.Substring(start, count));
    }

    [Fact]
    public void Matcher_ignores_case_unless_asked_and_trims_the_term()
    {
        const string text = "Bolt bolt BOLT";
        Assert.Equal(3, PdfTextMatcher.Find(text, " bolt  ", PdfSearchOptions.None).Count);
        Assert.Single(PdfTextMatcher.Find(text, "bolt", PdfSearchOptions.MatchCase));
    }

    [Fact]
    public void Matcher_whole_word_skips_matches_inside_longer_words()
    {
        const string text = "bolts bolt abolt bolt.";
        var hits = PdfTextMatcher.Find(text, "bolt", PdfSearchOptions.MatchWholeWord);

        Assert.Equal([6, 17], hits.Select(h => h.Start).ToArray());
    }

    [Fact]
    public void Matcher_counts_overlapping_matches_only_when_consecutive_is_set()
    {
        Assert.Single(PdfTextMatcher.Find("aaa", "aa", PdfSearchOptions.None));
        Assert.Equal(2, PdfTextMatcher.Find("aaa", "aa", PdfSearchOptions.ConsecutiveMatches).Count);
    }

    [Fact]
    public void Matcher_returns_nothing_for_a_blank_term_or_empty_text()
    {
        Assert.Empty(PdfTextMatcher.Find("some text", "   ", PdfSearchOptions.None));
        Assert.Empty(PdfTextMatcher.Find(string.Empty, "x", PdfSearchOptions.None));
    }

    // --- PdfPageTextCache -------------------------------------------------------------------

    [Fact]
    public void Cache_drops_everything_on_a_newer_version_and_ignores_stale_stores()
    {
        var cache = new PdfPageTextCache();
        cache.Store(1, 0, "old");
        Assert.True(cache.TryGet(1, 0, out string hit));
        Assert.Equal("old", hit);

        Assert.False(cache.TryGet(2, 0, out _)); // newer version: entry gone
        cache.Store(1, 0, "read before the edit");  // stale writer finishing late
        Assert.False(cache.TryGet(2, 0, out _));
        Assert.Equal(0, cache.Count);
    }

    // --- PdfSearch over the cache ----------------------------------------------------------

    [Fact]
    public void A_warm_search_opens_only_the_pages_that_match()
    {
        using var ws = new TempWorkspace();
        var pages = Enumerable.Range(0, 30).Select(i => i == 20 ? "the anchor bolt" : $"page {i} text").ToArray();
        string path = TestPdfBuilder.WritePdf(ws.Path("warm.pdf"), pages);
        using var doc = PdfDocument.Load(path);

        var cold = new PdfSearch(doc);
        Assert.Single(cold.FindAll("anchor"));
        Assert.Equal(30, cold.PagesOpened); // first search reads every page once, as before

        var warm = new PdfSearch(doc);
        SearchHit hit = Assert.Single(warm.FindAll("anchor"));
        Assert.Equal(1, warm.PagesOpened);  // only page 20 needed PDFium (for its highlight box)
        Assert.Equal(20, hit.PageIndex);
        Assert.NotEmpty(hit.Rects);

        var none = new PdfSearch(doc);
        Assert.Empty(none.FindAll("nowhere"));
        Assert.Equal(0, none.PagesOpened);
    }

    [Fact]
    public void Cached_and_cold_searches_return_the_same_ranges_and_boxes()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("same.pdf"),
            ["alpha needle beta", "gamma", "needle needle delta"]);
        using var doc = PdfDocument.Load(path);

        var cold = new PdfSearch(doc).FindAll("needle");
        var warm = new PdfSearch(doc).FindAll("needle");

        Assert.Equal(cold.Select(h => (h.PageIndex, h.CharStart, h.CharCount)),
                     warm.Select(h => (h.PageIndex, h.CharStart, h.CharCount)));
        Assert.Equal(cold.Select(h => h.Rects.Count), warm.Select(h => h.Rects.Count));
    }

    [Fact]
    public void Progressive_search_starts_at_the_given_page_wraps_and_returns_sorted_hits()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("prog.pdf"),
            ["intro", "needle one", "filler", "filler", "needle two", "filler"]);
        using var doc = PdfDocument.Load(path);

        var batches = new List<IReadOnlyList<SearchHit>>();
        var all = new PdfSearch(doc).Search("needle", PdfSearchOptions.None, startPage: 3, batches.Add);

        Assert.Equal([1, 4], all.Select(h => h.PageIndex).ToArray());
        Assert.NotEmpty(batches);
        Assert.Equal(4, batches[0][0].PageIndex);   // nearest the reader first…
        Assert.Equal(2, batches.Sum(b => b.Count)); // …and every hit reported exactly once
    }

    [Fact]
    public void An_edit_invalidates_cached_page_text()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("edit.pdf"), ["first page", "second page"]);
        using var doc = PdfDocument.Load(path);

        Assert.Contains("first", PdfTextExtractor.GetPageText(doc, 0));
        doc.DeletePages([0]);

        Assert.Contains("second", PdfTextExtractor.GetPageText(doc, 0));
        Assert.Empty(new PdfSearch(doc).FindAll("first"));
    }
}
