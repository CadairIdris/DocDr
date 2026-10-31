using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

public sealed class MeasureTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PdfDocument _doc;

    public MeasureTests() =>
        _doc = PdfDocument.Load(TestPdfBuilder.WritePdf(_dir.File("m.pdf"), ["one", "two"]));

    public void Dispose()
    {
        _doc.Dispose();
        _dir.Dispose();
    }

    private PdfPaneViewModel Pane(ShapeTool tool, PdfMeasureScale? scale = null)
    {
        var pane = new PdfPaneViewModel("Left", _doc, _doc.GetPageSizes(), new FakeRenderQueue())
        {
            ShapeTool = tool,
        };
        if (scale is not null)
        {
            pane.ScaleProvider = _ => scale;
        }

        return pane;
    }

    private PdfAnnotation Only(int page = 0) => Assert.Single(_doc.GetAnnotations(page));

    [Fact]
    public void Distance_finishes_on_the_second_click_at_the_page_scale_and_stays_armed()
    {
        PdfMeasureScale scale = PdfMeasureScale.FromDrawingRatio(50, "m");
        PdfPaneViewModel pane = Pane(ShapeTool.MeasureDistance, scale);

        pane.MeasureClick(0, new PdfPoint(100, 100), constrain: false);
        Assert.Empty(_doc.GetAnnotations(0));
        pane.MeasureClick(0, new PdfPoint(300, 100), constrain: false);

        PdfAnnotation a = Only();
        Assert.Equal(PdfAnnotationKind.MeasureDistance, a.Kind);
        Assert.Equal(2, a.Vertices.Count);
        Assert.Equal(scale, a.Scale);
        Assert.Equal(ShapeTool.MeasureDistance, pane.ShapeTool);
        Assert.False(pane.HasMeasureInProgress);
    }

    [Fact]
    public void Perimeter_takes_clicks_until_finished_and_backspace_drops_the_last_vertex()
    {
        PdfPaneViewModel pane = Pane(ShapeTool.MeasurePerimeter);
        pane.MeasureClick(0, new PdfPoint(50, 50), false);
        pane.MeasureClick(0, new PdfPoint(150, 50), false);
        pane.MeasureClick(0, new PdfPoint(150, 150), false);
        pane.MeasureClick(0, new PdfPoint(10, 10), false);
        pane.MeasureUndoVertex();
        pane.MeasureClick(0, new PdfPoint(150, 150), false); // same spot as the last vertex: ignored
        pane.MeasureFinish();

        PdfAnnotation a = Only();
        Assert.Equal(PdfAnnotationKind.MeasurePerimeter, a.Kind);
        Assert.Equal(3, a.Vertices.Count);
    }

    [Fact]
    public void An_area_needs_three_points()
    {
        PdfPaneViewModel pane = Pane(ShapeTool.MeasureArea);
        pane.MeasureClick(0, new PdfPoint(50, 50), false);
        pane.MeasureClick(0, new PdfPoint(150, 50), false);
        pane.MeasureFinish();
        Assert.Empty(_doc.GetAnnotations(0));

        pane.MeasureClick(0, new PdfPoint(50, 50), false);
        pane.MeasureClick(0, new PdfPoint(150, 50), false);
        pane.MeasureClick(0, new PdfPoint(150, 150), false);
        pane.MeasureFinish();
        Assert.Equal(PdfAnnotationKind.MeasureArea, Only().Kind);
    }

    [Fact]
    public void Esc_abandons_and_switching_tools_clears_the_measurement_in_progress()
    {
        PdfPaneViewModel pane = Pane(ShapeTool.MeasurePerimeter);
        pane.MeasureClick(0, new PdfPoint(50, 50), false);
        pane.MeasureCancel();
        Assert.False(pane.HasMeasureInProgress);

        pane.MeasureClick(0, new PdfPoint(50, 50), false);
        pane.ShapeTool = ShapeTool.None;
        Assert.False(pane.HasMeasureInProgress);
        Assert.Empty(_doc.GetAnnotations(0));
    }

    [Fact]
    public void Shift_constrains_a_segment_to_45_degrees()
    {
        PdfPaneViewModel pane = Pane(ShapeTool.MeasureDistance);
        pane.MeasureClick(0, new PdfPoint(100, 100), false);
        pane.MeasureClick(0, new PdfPoint(300, 110), constrain: true);

        Assert.Equal(100, Only().Vertices[1].Y, 6);
    }

    [Fact]
    public void A_click_on_another_page_starts_over_there()
    {
        PdfPaneViewModel pane = Pane(ShapeTool.MeasurePerimeter);
        pane.MeasureClick(0, new PdfPoint(50, 50), false);
        pane.MeasureClick(1, new PdfPoint(60, 60), false);
        pane.MeasureClick(1, new PdfPoint(160, 60), false);
        pane.MeasureFinish();

        Assert.Empty(_doc.GetAnnotations(0));
        Assert.Equal(2, Only(1).Vertices.Count);
    }

    [Fact]
    public void Calibration_reports_the_drawn_length_and_disarms()
    {
        PdfPaneViewModel pane = Pane(ShapeTool.Calibrate);
        (int Page, double Points)? reported = null;
        pane.CalibrationMeasured += (page, pts) => reported = (page, pts);

        pane.MeasureClick(0, new PdfPoint(100, 100), false);
        pane.MeasureClick(0, new PdfPoint(100, 300), false);

        Assert.Equal((0, 200.0), reported);
        Assert.Equal(ShapeTool.None, pane.ShapeTool);
        Assert.Empty(_doc.GetAnnotations(0)); // calibrating adds nothing to the page
    }

    [Fact]
    public void A_measurement_is_selectable_by_its_line_and_movable()
    {
        PdfPaneViewModel pane = Pane(ShapeTool.MeasureDistance);
        pane.MeasureClick(0, new PdfPoint(100, 100), false);
        pane.MeasureClick(0, new PdfPoint(300, 100), false);
        PdfAnnotation a = Only();

        Assert.True(PdfPaneViewModel.HitsMeasurement(a, new PdfPoint(200, 102), tolerance: 4));
        Assert.False(PdfPaneViewModel.HitsMeasurement(a, new PdfPoint(200, 160), tolerance: 4));

        PdfAnnotation moved = PdfPaneViewModel.MoveShape(a, 10, -5);
        Assert.Equal(new PdfPoint(110, 95), moved.Vertices[0]);
        PdfAnnotation dragged = PdfPaneViewModel.MoveLineEndpoint(a, 1, 50, 0);
        Assert.Equal(new PdfPoint(350, 100), dragged.Vertices[1]);
    }

    [Fact]
    public void Scale_dialog_parses_a_ratio_and_a_calibration()
    {
        var ratio = new MeasureScaleViewModel(PdfMeasureScale.Default, null, "3") { RatioText = "1:100", Unit = "m" };
        ratio.ApplyCommand.Execute(null);
        Assert.Equal(MeasureScaleOutcome.Apply, ratio.Outcome);
        Assert.Equal("1:100", ratio.Result!.Label);

        var cal = new MeasureScaleViewModel(PdfMeasureScale.Default, calibrationPoints: 144, "3")
        {
            KnownLengthText = "6", Unit = "m",
        };
        cal.ApplyCommand.Execute(null);
        Assert.Equal(6.0 / 144, cal.Result!.UnitsPerPoint, 9);

        var bad = new MeasureScaleViewModel(PdfMeasureScale.Default, null, "3") { RatioText = "abc" };
        bad.ApplyCommand.Execute(null);
        Assert.Equal(MeasureScaleOutcome.Cancel, bad.Outcome);
        Assert.NotEmpty(bad.Error);
    }

    [Fact]
    public void Csv_export_has_one_row_per_measurement_and_neutralises_formulas()
    {
        PdfAnnotation a = PdfAnnotation.NewMeasurement(PdfAnnotationKind.MeasureDistance,
            [new(0, 0), new(72, 0)], 0xFFC62828, PdfMeasureScale.Default, author: "=HYPERLINK(\"x\")");

        string csv = DocumentTabViewModel.MeasurementsCsv([(0, a)], i => (i + 1).ToString());
        string[] lines = csv.TrimEnd().Split(Environment.NewLine);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("1,Distance,25.4,mm,1:1,2,", lines[1]);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", lines[1]);
    }
}

public sealed class MeasureSnapTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly PdfDocument _doc;

    public MeasureSnapTests() =>
        _doc = PdfDocument.Load(TestPdfBuilder.WritePdf(_dir.File("s.pdf"), ["one"]));

    public void Dispose()
    {
        _doc.Dispose();
        _dir.Dispose();
    }

    private PdfPaneViewModel Pane()
    {
        var pane = new PdfPaneViewModel("Left", _doc, _doc.GetPageSizes(), new FakeRenderQueue())
        {
            ShapeTool = ShapeTool.MeasureDistance,
        };
        pane.SetSnapGeometry(0, PdfSnapGeometry.FromSegments(
        [
            (new PdfPoint(100, 100), new PdfPoint(300, 100)),
            (new PdfPoint(300, 100), new PdfPoint(300, 200)),
        ]));
        return pane;
    }

    [Fact]
    public void Clicks_near_drawn_corners_measure_corner_to_corner()
    {
        PdfPaneViewModel pane = Pane();
        pane.MeasureClick(0, new PdfPoint(101.5, 98.7), constrain: false);
        pane.MeasureClick(0, new PdfPoint(298.9, 101.2), constrain: false);

        PdfAnnotation a = Assert.Single(_doc.GetAnnotations(0));
        Assert.Equal(new PdfPoint(100, 100), a.Vertices[0]);
        Assert.Equal(new PdfPoint(300, 100), a.Vertices[1]);
    }

    [Fact]
    public void Ctrl_or_turning_snapping_off_leaves_the_click_where_it_was()
    {
        PdfPaneViewModel pane = Pane();
        pane.MeasureClick(0, new PdfPoint(101.5, 98.7), constrain: false, suspendSnap: true);
        pane.SnapEnabled = false;
        pane.MeasureClick(0, new PdfPoint(298.9, 101.2), constrain: false);

        PdfAnnotation a = Assert.Single(_doc.GetAnnotations(0));
        Assert.Equal(new PdfPoint(101.5, 98.7), a.Vertices[0]);
        Assert.Equal(new PdfPoint(298.9, 101.2), a.Vertices[1]);
    }

    [Fact]
    public void Hovering_with_a_tool_armed_shows_the_snap_marker()
    {
        PdfPaneViewModel pane = Pane();
        pane.MeasureHover(0, new PdfPoint(299, 199), constrain: false);
        Assert.NotNull(pane.Pages[0].SnapMarker);
        Assert.Equal("Endpoint", pane.Pages[0].SnapKind);

        pane.MeasurePointerLeft();
        Assert.Null(pane.Pages[0].SnapMarker);
    }

    private PdfAnnotation PlaceFreeDistance(PdfPaneViewModel pane)
    {
        pane.MeasureClick(0, new PdfPoint(150, 150), constrain: false, suspendSnap: true);
        pane.MeasureClick(0, new PdfPoint(250, 150), constrain: false, suspendSnap: true);
        pane.ShapeTool = ShapeTool.None;
        return Assert.Single(_doc.GetAnnotations(0));
    }

    [Fact]
    public void A_dragged_vertex_snaps_onto_a_drawn_corner()
    {
        PdfPaneViewModel pane = Pane();
        PdfAnnotation a = PlaceFreeDistance(pane);

        // Drag the second end from (250,150) to near the (300,200) corner.
        pane.BeginLineEndpointMove(a.Id, 1);
        pane.PreviewLineEndpointMove(48.6, 51.1);
        Assert.Equal("Endpoint", pane.Pages[0].SnapKind);
        pane.EndLineEndpointMove(48.6, 51.1);

        PdfAnnotation moved = Assert.Single(_doc.GetAnnotations(0));
        Assert.Equal(new PdfPoint(300, 200), moved.Vertices[1]);
        Assert.Equal(new PdfPoint(150, 150), moved.Vertices[0]);
        Assert.Null(pane.Pages[0].SnapMarker); // cleared at the drop
    }

    [Fact]
    public void Ctrl_drags_a_vertex_freely()
    {
        PdfPaneViewModel pane = Pane();
        PdfAnnotation a = PlaceFreeDistance(pane);

        pane.BeginLineEndpointMove(a.Id, 1);
        pane.EndLineEndpointMove(48.6, 51.1, suspendSnap: true);

        PdfPoint v = Assert.Single(_doc.GetAnnotations(0)).Vertices[1];
        Assert.Equal(298.6, v.X, 6);
        Assert.Equal(201.1, v.Y, 6);
    }

    [Fact]
    public void A_plain_line_end_is_not_snapped()
    {
        PdfPaneViewModel pane = Pane();
        pane.ShapeTool = ShapeTool.None;
        _doc.AddAnnotation(0, PdfAnnotation.NewLine(new PdfPoint(150, 150), new PdfPoint(250, 150), 0xFF000000));
        PdfAnnotation line = Assert.Single(_doc.GetAnnotations(0));

        pane.BeginLineEndpointMove(line.Id, 1);
        pane.EndLineEndpointMove(48.6, 51.1);

        PdfPoint end = Assert.Single(_doc.GetAnnotations(0)).Leader[1];
        Assert.Equal(298.6, end.X, 6);
        Assert.Equal(201.1, end.Y, 6);
    }
}
