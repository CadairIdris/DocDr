using DocDr.Pdf;

namespace DocDr.Pdf.Tests;

/// <summary>The per-annotation styling fields (line width, fill, dashed, borderless, text colour)
/// survive a save + reload through the private <c>/DocDrShape</c> key.</summary>
public sealed class PdfShapeFormatTests
{
    private static PdfAnnotation RoundTrip(PdfAnnotation annotation)
    {
        using PdfDocument doc = PdfDocument.CreateBlank(new PdfSize(400, 400));
        doc.AddAnnotation(0, annotation);
        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());
        return Assert.Single(reloaded.GetAnnotations(0));
    }

    [Fact]
    public void Tick_round_trips_with_its_box_colour_and_width()
    {
        var box = new PdfRect(100, 220, 118, 202);
        PdfAnnotation a = RoundTrip(PdfAnnotation.NewTick(box, 0xFF2E7D32, "Rob"));

        Assert.Equal(PdfAnnotationKind.Tick, a.Kind);
        Assert.Equal(box.Left, a.Box.Left, 1);
        Assert.Equal(box.Top, a.Box.Top, 1);
        Assert.Equal(box.Right, a.Box.Right, 1);
        Assert.Equal(box.Bottom, a.Box.Bottom, 1);
        Assert.Equal(0xFF2E7D32u, a.ColorArgb | 0xFF000000u);
        Assert.Equal(PdfAnnotation.DefaultTickLineWidth, a.LineWidth, 1);
    }

    [Fact]
    public void Tick_points_sit_inside_the_box_short_stroke_down_long_stroke_up()
    {
        IReadOnlyList<PdfPoint> p = PdfAnnotation.TickPoints(new PdfRect(0, 100, 100, 0));

        Assert.Equal(3, p.Count);
        Assert.All(p, pt => Assert.InRange(pt.X, 0, 100));
        Assert.All(p, pt => Assert.InRange(pt.Y, 0, 100));
        Assert.True(p[1].Y < p[0].Y && p[1].Y < p[2].Y); // the vertex is the lowest point
        Assert.True(p[2].Y - p[1].Y > p[0].Y - p[1].Y);  // the right stroke is the long one
    }

    [Fact]
    public void A_ticked_page_renders_differently_from_a_clean_one()
    {
        using PdfDocument doc = PdfDocument.CreateBlank(new PdfSize(200, 200));
        var renderer = new PageRenderer();
        RenderedPage before = doc.WithAnnotationsBaked(() => renderer.Render(doc, 0, 200, 200));
        doc.AddAnnotation(0, PdfAnnotation.NewTick(new PdfRect(60, 140, 140, 60), 0xFF000000));
        RenderedPage after = doc.WithAnnotationsBaked(() => renderer.Render(doc, 0, 200, 200));

        Assert.NotEqual(before.Pixels.ToArray(), after.Pixels.ToArray());
    }

    [Fact]
    public void Rectangle_line_width_and_dash_round_trip()
    {
        PdfAnnotation a = RoundTrip(PdfAnnotation.NewRectangle(new PdfRect(40, 300, 220, 120), 0xFF2244AA, "Rob")
            with { LineWidth = 3, Dashed = true });

        Assert.Equal(PdfAnnotationKind.Rectangle, a.Kind);
        Assert.Equal(3, a.LineWidth, 1);
        Assert.True(a.Dashed);
    }

    [Fact]
    public void Ellipse_fill_round_trips_and_clears()
    {
        PdfAnnotation filled = RoundTrip(PdfAnnotation.NewEllipse(new PdfRect(40, 300, 220, 120), 0xFF2244AA, "Rob")
            with { FillArgb = 0x40FF0000 });
        Assert.Equal(0x40FF0000u, filled.FillArgb);

        PdfAnnotation none = RoundTrip(PdfAnnotation.NewEllipse(new PdfRect(40, 300, 220, 120), 0xFF2244AA, "Rob"));
        Assert.Null(none.FillArgb);
    }

    [Fact]
    public void Line_dashed_round_trips()
    {
        PdfAnnotation a = RoundTrip(PdfAnnotation.NewArrow(new PdfPoint(30, 40), new PdfPoint(300, 350), 0xFF112233, "Rob")
            with { Dashed = true, LineWidth = 2 });

        Assert.Equal(PdfAnnotationKind.Arrow, a.Kind);
        Assert.True(a.Dashed);
        Assert.Equal(2, a.LineWidth, 1);
    }

    [Fact]
    public void Text_box_borderless_and_text_colour_round_trip()
    {
        PdfAnnotation a = RoundTrip(PdfAnnotation.NewTextBox(
            new PdfRect(40, 300, 240, 220), "Hello", 0xFF2244AA, PdfAnnotation.DefaultFontSize, "Rob")
            with { Borderless = true, TextColorArgb = 0xFF000000 });

        Assert.Equal(PdfAnnotationKind.TextBox, a.Kind);
        Assert.True(a.Borderless);
        Assert.Equal(0xFF000000u, a.TextColorArgb);
    }

    [Fact]
    public void Defaults_stay_default_after_round_trip()
    {
        PdfAnnotation a = RoundTrip(PdfAnnotation.NewRectangle(new PdfRect(40, 300, 220, 120), 0xFF2244AA, "Rob"));

        Assert.Equal(0, a.LineWidth);
        Assert.Equal(PdfAnnotation.DefaultLineWidth, a.EffectiveLineWidth);
        Assert.False(a.Dashed);
        Assert.False(a.Borderless);
        Assert.Null(a.FillArgb);
        Assert.Null(a.TextColorArgb);
    }
}
