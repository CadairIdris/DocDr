namespace DocDr.Pdf.Tests;

public sealed class PdfMeasureTests
{
    private const double MmPerPoint = 25.4 / 72;

    [Fact]
    public void Default_scale_measures_the_sheet_in_millimetres()
    {
        PdfMeasureScale s = PdfMeasureScale.Default;
        Assert.Equal("mm", s.Unit);
        Assert.Equal(MmPerPoint, s.UnitsPerPoint, 9);
        // 72 pt = 1 in = 25.4 mm on paper.
        Assert.Equal(25.4, PdfMeasure.Value(PdfAnnotationKind.MeasureDistance, [new(0, 0), new(72, 0)], s), 6);
    }

    [Fact]
    public void A_1_to_50_drawing_in_metres_converts_paper_points_to_real_metres()
    {
        PdfMeasureScale s = PdfMeasureScale.FromDrawingRatio(50, "m");
        Assert.Equal("1:50", s.Label);

        // 100 mm on the sheet at 1:50 is 5 m.
        double hundredMmInPoints = 100 / MmPerPoint;
        double v = PdfMeasure.Value(PdfAnnotationKind.MeasureDistance, [new(0, 0), new(hundredMmInPoints, 0)], s);
        Assert.Equal(5.0, v, 6);
    }

    [Fact]
    public void Calibration_maps_the_drawn_line_to_the_known_length()
    {
        PdfMeasureScale s = PdfMeasureScale.Calibrate(pageDistancePoints: 200, realLength: 4, unit: "m");
        Assert.Equal(4.0, PdfMeasure.Value(PdfAnnotationKind.MeasureDistance, [new(10, 10), new(210, 10)], s), 9);
        Assert.Equal(1.0, PdfMeasure.Value(PdfAnnotationKind.MeasureDistance, [new(0, 0), new(0, 50)], s), 9);
    }

    [Fact]
    public void Perimeter_sums_segments_and_area_uses_the_shoelace_in_squared_units()
    {
        PdfMeasureScale s = PdfMeasureScale.Calibrate(100, 1, "m"); // 100 pt = 1 m
        PdfPoint[] square = [new(0, 0), new(100, 0), new(100, 100), new(0, 100)];

        Assert.Equal(3.0, PdfMeasure.Value(PdfAnnotationKind.MeasurePerimeter, square, s), 9); // open: 3 sides
        Assert.Equal(1.0, PdfMeasure.Value(PdfAnnotationKind.MeasureArea, square, s), 9);
        Assert.Equal(1.0, PdfMeasure.Value(PdfAnnotationKind.MeasureArea, Enumerable.Reverse(square).ToArray(), s), 9);
        Assert.EndsWith("m²", PdfMeasure.Format(PdfAnnotationKind.MeasureArea, 1, s));
    }

    [Fact]
    public void Constrain45_snaps_the_angle_and_keeps_the_length()
    {
        PdfPoint p = PdfMeasure.Constrain45(new PdfPoint(0, 0), new PdfPoint(100, 8));
        Assert.Equal(0, p.Y, 6);
        Assert.Equal(Math.Sqrt((100 * 100) + (8 * 8)), p.X, 6);

        PdfPoint d = PdfMeasure.Constrain45(new PdfPoint(0, 0), new PdfPoint(50, 45));
        Assert.Equal(d.X, d.Y, 6);
    }

    [Fact]
    public void Area_caption_sits_at_the_centroid()
    {
        PdfPoint c = PdfMeasure.CaptionAnchor(PdfAnnotationKind.MeasureArea,
            [new(0, 0), new(100, 0), new(100, 50), new(0, 50)]);
        Assert.Equal(50, c.X, 6);
        Assert.Equal(25, c.Y, 6);
    }

    [Fact]
    public void Bad_scales_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => PdfMeasureScale.FromDrawingRatio(0, "m"));
        Assert.Throws<ArgumentException>(() => PdfMeasureScale.FromRatio(1, "furlong", 1, "m"));
        Assert.Throws<ArgumentException>(() => PdfMeasureScale.Calibrate(0, 1, "m"));
        Assert.Throws<ArgumentException>(() =>
            PdfAnnotation.NewMeasurement(PdfAnnotationKind.MeasureArea, [new(0, 0), new(1, 1)], 0xFF000000, PdfMeasureScale.Default));
    }

    [Fact]
    public void Measurements_round_trip_with_their_points_and_scale()
    {
        PdfMeasureScale scale = PdfMeasureScale.FromDrawingRatio(100, "m", decimals: 3);
        PdfPoint[] pts = [new(50, 50), new(250, 50), new(250, 150)];
        byte[] saved;

        using (PdfDocument doc = PdfDocument.CreateBlank(new PdfSize(400, 400)))
        {
            doc.AddAnnotation(0, PdfAnnotation.NewMeasurement(PdfAnnotationKind.MeasurePerimeter, pts, 0xFFC62828, scale, "rob"));
            saved = doc.SaveToBytes();
        }

        using PdfDocument reloaded = PdfDocument.Load(saved);
        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(0));
        Assert.Equal(PdfAnnotationKind.MeasurePerimeter, a.Kind);
        Assert.Equal(pts.Length, a.Vertices.Count);
        Assert.Equal(250, a.Vertices[1].X, 2);
        Assert.NotNull(a.Scale);
        Assert.Equal(scale.UnitsPerPoint, a.Scale!.UnitsPerPoint, 9);
        Assert.Equal("m", a.Scale.Unit);
        Assert.Equal("1:100", a.Scale.Label);
        Assert.Equal(3, a.Scale.Decimals);
        Assert.Null(a.Contents); // the generated caption isn't read back as a user note
        Assert.Equal(PdfMeasure.Caption(a), PdfMeasure.Format(a.Kind,
            PdfMeasure.Value(a.Kind, pts, scale), scale));
    }

    [Fact]
    public void A_measured_page_renders_differently_from_a_clean_one()
    {
        using PdfDocument doc = PdfDocument.CreateBlank(new PdfSize(200, 200));
        var renderer = new PageRenderer();
        RenderedPage before = doc.WithAnnotationsBaked(() => renderer.Render(doc, 0, 200, 200));
        doc.AddAnnotation(0, PdfAnnotation.NewMeasurement(PdfAnnotationKind.MeasureArea,
            [new(30, 30), new(170, 30), new(170, 170), new(30, 170)], 0xFF1565C0, PdfMeasureScale.Default));
        RenderedPage after = doc.WithAnnotationsBaked(() => renderer.Render(doc, 0, 200, 200));

        Assert.NotEqual(before.Pixels.ToArray(), after.Pixels.ToArray());
    }
}
