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
