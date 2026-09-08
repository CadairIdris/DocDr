using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

/// <summary>Pure geometry / classification helpers on <see cref="PdfPaneViewModel"/>. The stateful
/// layout paths are covered by hand against a real document; these are the bits that are safe to
/// pin down in isolation.</summary>
public sealed class PdfPaneViewModelTests
{
    [Theory]
    [InlineData(800, 600, 4800, 800, 600)]      // under the cap — untouched
    [InlineData(9600, 4800, 4800, 4800, 2400)]  // long edge clamped, aspect kept
    [InlineData(2400, 9600, 4800, 1200, 4800)]
    public void CapToMaxEdge_clamps_the_long_edge_and_preserves_aspect(
        double w, double h, double maxEdge, int expectedW, int expectedH)
    {
        (int width, int height) = PdfPaneViewModel.CapToMaxEdge(w, h, maxEdge);
        Assert.Equal(expectedW, width);
        Assert.Equal(expectedH, height);
    }

    [Theory]
    [InlineData(PdfAnnotationKind.Rectangle, true)]
    [InlineData(PdfAnnotationKind.Line, true)]
    [InlineData(PdfAnnotationKind.TextBox, true)]
    [InlineData(PdfAnnotationKind.Highlight, true)]
    [InlineData(PdfAnnotationKind.Comment, true)]
    [InlineData(PdfAnnotationKind.Ink, false)]
    [InlineData(PdfAnnotationKind.Image, false)]
    public void HasFormatBar_is_off_only_for_ink_and_image(PdfAnnotationKind kind, bool expected) =>
        Assert.Equal(expected, PdfPaneViewModel.HasFormatBar(kind));

    [Fact]
    public void MoveShape_shifts_a_rectangle_box_by_the_delta()
    {
        PdfAnnotation a = PdfAnnotation.NewRectangle(new PdfRect(40, 300, 220, 120), 0xFF2244AA);

        PdfAnnotation moved = PdfPaneViewModel.MoveShape(a, 10, -5);

        Assert.Equal(new PdfRect(50, 295, 230, 115), moved.Box);
    }

    [Fact]
    public void MoveShape_shifts_both_endpoints_of_a_line_together()
    {
        PdfAnnotation a = PdfAnnotation.NewLine(new PdfPoint(30, 40), new PdfPoint(300, 350), 0xFF112233);

        PdfAnnotation moved = PdfPaneViewModel.MoveShape(a, 5, 7);

        // Leader is [to, from] — index 0 is the arrowhead end.
        Assert.Equal(new PdfPoint(305, 357), moved.Leader[0]);
        Assert.Equal(new PdfPoint(35, 47), moved.Leader[1]);
    }

    [Fact]
    public void MoveLineEndpoint_moves_only_the_named_end()
    {
        PdfAnnotation a = PdfAnnotation.NewArrow(new PdfPoint(0, 0), new PdfPoint(100, 100), 0xFF112233);

        // Leader is [to, from]; move index 1 (the "from" end).
        PdfAnnotation moved = PdfPaneViewModel.MoveLineEndpoint(a, 1, 10, -10);

        Assert.Equal(new PdfPoint(100, 100), moved.Leader[0]);
        Assert.Equal(new PdfPoint(10, -10), moved.Leader[1]);
    }

    [Fact]
    public void MoveLineEndpoint_ignores_an_out_of_range_end()
    {
        PdfAnnotation a = PdfAnnotation.NewLine(new PdfPoint(0, 0), new PdfPoint(1, 1), 0xFF112233);
        Assert.Same(a, PdfPaneViewModel.MoveLineEndpoint(a, 2, 1, 1));
    }

    [Fact]
    public void ResizeShape_drags_a_corner_and_enforces_the_minimum_box()
    {
        PdfAnnotation a = PdfAnnotation.NewRectangle(new PdfRect(0, 100, 200, 0), 0xFF2244AA);

        PdfAnnotation wide = PdfPaneViewModel.ResizeShape(a, BoxHandle.BottomRight, 40, 0);
        Assert.Equal(240, wide.Box.Right);

        // Pull the right edge past the left — clamped to the minimum width.
        PdfAnnotation squashed = PdfPaneViewModel.ResizeShape(a, BoxHandle.BottomRight, -400, 0);
        Assert.True(squashed.Box.Width >= 24);
    }
}
