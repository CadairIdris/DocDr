namespace DocDr.Pdf.Tests;

public sealed class PdfTextExtractorMergeTests
{
    [Fact]
    public void MergeIntoLineRects_unions_boxes_on_the_same_line_into_one_rect()
    {
        PdfRect[] boxes =
        [
            new PdfRect(Left: 0, Top: 10, Right: 5, Bottom: 0),
            new PdfRect(Left: 5, Top: 10, Right: 10, Bottom: 0),
            new PdfRect(Left: 10, Top: 11, Right: 15, Bottom: 1), // slightly higher, still overlaps
        ];

        var merged = PdfTextExtractor.MergeIntoLineRects(boxes);

        // The tight union is (0, 11, 15, 0) — a small symmetric pad is added around it so the
        // highlight doesn't sit flush against the glyph edges.
        var rect = Assert.Single(merged);
        Assert.True(rect.Left < 0);
        Assert.True(rect.Right > 15);
        Assert.True(rect.Bottom < 0);
        Assert.True(rect.Top > 11);
        Assert.Equal(0 - rect.Left, rect.Right - 15, precision: 6);
        Assert.Equal(0 - rect.Bottom, rect.Top - 11, precision: 6);
    }

    [Fact]
    public void MergeIntoLineRects_starts_a_new_rect_on_a_real_line_break()
    {
        PdfRect[] boxes =
        [
            new PdfRect(Left: 0, Top: 10, Right: 5, Bottom: 0),
            new PdfRect(Left: 5, Top: 10, Right: 10, Bottom: 0),
            // A full line below — no vertical overlap with the run above.
            new PdfRect(Left: 0, Top: -2, Right: 5, Bottom: -12),
        ];

        var merged = PdfTextExtractor.MergeIntoLineRects(boxes);

        // Still two separate lines — padding is added after the same-line decision, so it can't
        // make a real break merge back together.
        Assert.Equal(2, merged.Count);
        Assert.True(merged[0].Right > 10);
        Assert.True(merged[1].Bottom < -12);
    }

    [Fact]
    public void MergeIntoLineRects_skips_degenerate_boxes_without_breaking_the_run()
    {
        PdfRect[] boxes =
        [
            new PdfRect(Left: 0, Top: 10, Right: 5, Bottom: 0),
            new PdfRect(Left: 5, Top: 0, Right: 8, Bottom: 0), // a space: real width, zero height
            new PdfRect(Left: 8, Top: 10, Right: 13, Bottom: 0),
        ];

        var merged = PdfTextExtractor.MergeIntoLineRects(boxes);

        var rect = Assert.Single(merged);
        Assert.True(rect.Left < 0);
        Assert.True(rect.Right > 13);
    }

    [Fact]
    public void MergeIntoLineRects_empty_input_returns_no_rects()
    {
        Assert.Empty(PdfTextExtractor.MergeIntoLineRects([]));
    }
}
