using System.Collections.Generic;
using System.Linq;
using Run = DocDr.TestSupport.TestPdfBuilder.Run;
using Rect = DocDr.TestSupport.TestPdfBuilder.Rect;

namespace DocDr.Pdf.Tests;

public sealed class PdfVisualCompareTests
{
    // --- pure diff ----------------------------------------------------------------------------

    /// <summary>A white BGRx image with black filled boxes (x, y, w, h).</summary>
    private static byte[] Image(int w, int h, params (int X, int Y, int W, int H)[] boxes)
    {
        var px = new byte[w * 4 * h];
        System.Array.Fill(px, (byte)255);
        foreach ((int bx, int by, int bw, int bh) in boxes)
        {
            for (int y = by; y < by + bh; y++)
            {
                for (int x = bx; x < bx + bw; x++)
                {
                    int o = (y * w * 4) + (x * 4);
                    (px[o], px[o + 1], px[o + 2]) = (0, 0, 0);
                }
            }
        }

        return px;
    }

    private static readonly VisualCompareOptions Pure = new() { Dpi = 72 };

    [Fact]
    public void Identical_images_have_no_changes()
    {
        byte[] a = Image(200, 200, (20, 20, 50, 4), (20, 60, 120, 4), (100, 100, 30, 30));
        VisualDiff d = PdfVisualCompare.Diff(a, (byte[])a.Clone(), 200, 200, 800, Pure);
        Assert.False(d.HasChanges);
        Assert.Equal(0, d.RemovedPixels + d.AddedPixels);
    }

    [Fact]
    public void A_moved_box_is_removed_where_it_was_and_added_where_it_went()
    {
        // Lots of fixed linework so the alignment step has no reason to shift the page.
        var fixedLines = Enumerable.Range(0, 8).Select(i => (10, 10 + (i * 20), 180, 2)).ToArray();
        byte[] a = Image(200, 200, [.. fixedLines, (30, 175, 12, 12)]);
        byte[] b = Image(200, 200, [.. fixedLines, (150, 175, 12, 12)]);
        VisualDiff d = PdfVisualCompare.Diff(a, b, 200, 200, 800, Pure);

        Assert.Equal((0, 0), (d.OffsetX, d.OffsetY));
        Assert.Equal(2, d.Regions.Count);
        VisualDiffRegion removed = d.Regions.Single(r => r.RemovedPixels > 0);
        VisualDiffRegion added = d.Regions.Single(r => r.AddedPixels > 0);
        Assert.InRange(removed.X, 16, 40);
        Assert.InRange(added.X, 136, 160);
        Assert.Equal(0, removed.AddedPixels);
        Assert.Equal(0, added.RemovedPixels);

        // Overlay colours: red where removed, blue where added, faint grey for common ink.
        int Px(int x, int y) => (y * 800) + (x * 4);
        Assert.Equal(220, d.Overlay[Px(35, 180) + 2]);   // red channel
        Assert.Equal(230, d.Overlay[Px(155, 180)]);      // blue channel
        byte grey = d.Overlay[Px(50, 10)];
        Assert.InRange(grey, 140, 200);
    }

    [Fact]
    public void A_whole_page_shift_is_aligned_away()
    {
        (int, int, int, int)[] drawing = [(20, 20, 140, 3), (20, 20, 3, 120), (60, 70, 50, 30), (130, 120, 25, 6)];
        byte[] a = Image(200, 200, drawing);
        byte[] b = Image(200, 200, [.. drawing.Select(r => (r.Item1 + 3, r.Item2 + 2, r.Item3, r.Item4))]);
        VisualDiff d = PdfVisualCompare.Diff(a, b, 200, 200, 800, Pure);

        Assert.Equal((3, 2), (d.OffsetX, d.OffsetY));
        Assert.False(d.HasChanges);
    }

    [Fact]
    public void With_tolerance_one_pixel_drift_is_not_a_change()
    {
        byte[] a = Image(100, 100, (10, 10, 60, 2), (10, 40, 2, 40), (50, 50, 20, 20));
        byte[] b = Image(100, 100, (10, 11, 60, 2), (11, 40, 2, 40), (50, 50, 21, 20));
        VisualDiff d = PdfVisualCompare.Diff(a, b, 100, 100, 400, Pure with { MaxShiftFraction = 0, TolerancePixels = 1 });
        Assert.False(d.HasChanges);
    }

    [Fact]
    public void Letters_of_one_changed_word_group_into_one_region()
    {
        var changed = new byte[200 * 50];
        foreach (int x0 in new[] { 20, 30, 40, 50 })
        {
            for (int y = 20; y < 28; y++)
            {
                for (int x = x0; x < x0 + 5; x++)
                {
                    changed[(y * 200) + x] = 1;
                }
            }
        }

        changed[(5 * 200) + 190] = 2; // a lone pixel: noise
        IReadOnlyList<VisualDiffRegion> regions = PdfVisualCompare.Regions(changed, 200, 50, minPixels: 6);
        VisualDiffRegion r = Assert.Single(regions);
        Assert.Equal(16, r.X);
        Assert.Equal(160, r.RemovedPixels);
    }

    // --- PDFs ---------------------------------------------------------------------------------

    private static IReadOnlyList<Run> Text(string changedWord) =>
    [
        new("1.1 Scope", 72, 760, 12),
        new("This drawing shows the general arrangement of the frame.", 72, 730, 10),
        new($"Column size {changedWord} at grid line C.", 72, 710, 10),
        new("All dimensions in millimetres.", 72, 690, 10),
    ];

    private static readonly Rect[] Frame = [new(60, 400, 540, 402), new(60, 400, 62, 650), new(538, 400, 540, 650), new(60, 648, 540, 650)];

    [Fact]
    public void Same_pdf_compares_clean()
    {
        using var ws = new TempWorkspace();
        using var a = PdfDocument.Load(TestPdfBuilder.WriteRuns(ws.Path("a.pdf"), Text("305x305"), Frame));
        using var b = PdfDocument.Load(TestPdfBuilder.WriteRuns(ws.Path("b.pdf"), Text("305x305"), Frame));
        VisualDiff d = PdfVisualCompare.ComparePages(a, 0, b, 0);
        Assert.False(d.HasChanges);
        Assert.False(d.SizesDiffer);
    }

    [Fact]
    public void A_changed_word_and_a_moved_beam_are_found_where_they_are()
    {
        using var ws = new TempWorkspace();
        Rect[] beamA = [.. Frame, new(100, 500, 300, 506)];
        Rect[] beamB = [.. Frame, new(100, 560, 300, 566)];
        using var a = PdfDocument.Load(TestPdfBuilder.WriteRuns(ws.Path("a.pdf"), Text("305x305"), beamA));
        using var b = PdfDocument.Load(TestPdfBuilder.WriteRuns(ws.Path("b.pdf"), Text("356x368"), beamB));
        VisualDiff d = PdfVisualCompare.ComparePages(a, 0, b, 0, new VisualCompareOptions());

        Assert.True(d.HasChanges);
        double s = 150 / 72.0; // px per point; y from the top of the 792-pt page
        bool Covers(double xPt, double yPtFromBottom) => d.Regions.Any(r =>
            r.X <= xPt * s && xPt * s <= r.X + r.Width
            && r.Y <= (792 - yPtFromBottom) * s && (792 - yPtFromBottom) * s <= r.Y + r.Height);

        Assert.True(Covers(165, 713), "the changed column size");
        Assert.True(Covers(200, 503), "where the beam was");
        Assert.True(Covers(200, 563), "where the beam went");
        Assert.False(Covers(150, 733), "the unchanged sentence");
    }

    [Theory]
    [InlineData("306")]
    [InlineData("308")]
    [InlineData("3O5")]
    public void One_changed_digit_in_small_text_is_found(string changed)
    {
        using var ws = new TempWorkspace();
        using var a = PdfDocument.Load(TestPdfBuilder.WriteRuns(ws.Path("a.pdf"), [new("Column size 305 at grid C", 72, 710, 9)]));
        using var b = PdfDocument.Load(TestPdfBuilder.WriteRuns(ws.Path("b.pdf"), [new($"Column size {changed} at grid C", 72, 710, 9)]));
        VisualDiff d = PdfVisualCompare.ComparePages(a, 0, b, 0);
        Assert.True(d.HasChanges);
    }

    [Fact]
    public void Document_overview_reports_changed_and_extra_pages()
    {
        using var ws = new TempWorkspace();
        using var a = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("a.pdf"), ["Page one text", "Page two text"]));
        using var b = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("b.pdf"), ["Page one text", "Page 2 is different now", "Page three"]));
        IReadOnlyList<PageCompareSummary> pages = PdfVisualCompare.CompareDocuments(a, b);

        Assert.Equal(
            [PageCompareStatus.Same, PageCompareStatus.Changed, PageCompareStatus.OnlyInNew],
            pages.Select(p => p.Status));
    }

    [Fact]
    public void Overview_with_an_offset_pairs_an_inserted_page_revision()
    {
        using var ws = new TempWorkspace();
        using var a = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("a.pdf"), ["Alpha", "Beta"]));
        using var b = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("b.pdf"), ["New cover sheet", "Alpha", "Beta"]));
        IReadOnlyList<PageCompareSummary> pages = PdfVisualCompare.CompareDocuments(a, b, newPageOffset: 1);

        Assert.Equal(PdfVisualCompare.PairCount(2, 3, 1), pages.Count);
        Assert.Equal(new PageCompareSummary(null, 0, PageCompareStatus.OnlyInNew, 0), pages[0]);
        Assert.Equal(new PageCompareSummary(0, 1, PageCompareStatus.Same, 0), pages[1]);
        Assert.Equal(new PageCompareSummary(1, 2, PageCompareStatus.Same, 0), pages[2]);
    }
}
