namespace DocDr.Pdf.Tests;

public sealed class PdfSnapGeometryTests
{
    // A 200 × 100 rectangle with both diagonals drawn.
    private static PdfSnapGeometry Box() => PdfSnapGeometry.FromSegments(
    [
        (new PdfPoint(100, 100), new PdfPoint(300, 100)),
        (new PdfPoint(300, 100), new PdfPoint(300, 200)),
        (new PdfPoint(300, 200), new PdfPoint(100, 200)),
        (new PdfPoint(100, 200), new PdfPoint(100, 100)),
        (new PdfPoint(100, 100), new PdfPoint(300, 200)),
        (new PdfPoint(100, 200), new PdfPoint(300, 100)),
    ]);

    [Fact]
    public void A_corner_wins_over_a_nearer_line()
    {
        PdfSnap s = Box().Snap(new PdfPoint(103, 101), tolerance: 6)!.Value;
        Assert.Equal(PdfSnapKind.Endpoint, s.Kind);
        Assert.Equal(new PdfPoint(100, 100), s.Point);
    }

    [Fact]
    public void Crossing_diagonals_snap_to_their_intersection()
    {
        PdfSnap s = Box().Snap(new PdfPoint(202, 151), tolerance: 6)!.Value;
        Assert.Equal(PdfSnapKind.Intersection, s.Kind);
        Assert.Equal(200, s.Point.X, 6);
        Assert.Equal(150, s.Point.Y, 6);
    }

    [Fact]
    public void Midpoints_then_the_nearest_point_on_a_line()
    {
        PdfSnapGeometry g = PdfSnapGeometry.FromSegments([(new PdfPoint(0, 0), new PdfPoint(100, 0))]);

        PdfSnap mid = g.Snap(new PdfPoint(52, 3), tolerance: 6)!.Value;
        Assert.Equal(PdfSnapKind.Midpoint, mid.Kind);
        Assert.Equal(new PdfPoint(50, 0), mid.Point);

        PdfSnap on = g.Snap(new PdfPoint(30, 2), tolerance: 6)!.Value;
        Assert.Equal(PdfSnapKind.OnLine, on.Kind);
        Assert.Equal(30, on.Point.X, 6);
        Assert.Equal(0, on.Point.Y, 6);

        Assert.Null(g.Snap(new PdfPoint(30, 40), tolerance: 6));
    }

    [Fact]
    public void A_long_line_is_found_far_from_its_ends()
    {
        PdfSnapGeometry g = PdfSnapGeometry.FromSegments([(new PdfPoint(0, 0), new PdfPoint(5000, 0))]);
        Assert.Equal(PdfSnapKind.OnLine, g.Snap(new PdfPoint(3333, 1), tolerance: 4)!.Value.Kind);
    }

    [Fact]
    public void Segment_count_is_capped()
    {
        var many = Enumerable.Range(0, PdfSnapGeometry.MaxSegments + 5)
            .Select(i => (new PdfPoint(i, 0), new PdfPoint(i, 10)));
        PdfSnapGeometry g = PdfSnapGeometry.FromSegments(many);

        Assert.True(g.Truncated);
        Assert.Equal(PdfSnapGeometry.MaxSegments, g.Segments.Count);
    }

    [Fact]
    public void Extract_reads_a_drawn_rectangle_from_the_page()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WriteRuns(ws.Path("rect.pdf"),
            [new TestPdfBuilder.Run("label", 100, 300)],
            [new TestPdfBuilder.Rect(100, 100, 300, 200)]);
        using PdfDocument doc = PdfDocument.Load(path);

        PdfSnapGeometry g = PdfSnapGeometry.Extract(doc, 0);

        Assert.True(g.Segments.Count >= 4); // the four edges of the filled rectangle
        PdfSnap s = g.Snap(new PdfPoint(298, 202), tolerance: 6)!.Value;
        Assert.Equal(PdfSnapKind.Endpoint, s.Kind);
        Assert.Equal(300, s.Point.X, 3);
        Assert.Equal(200, s.Point.Y, 3);
    }
}
