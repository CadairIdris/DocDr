using System.Linq;
using DocDr.Pdf;

namespace DocDr.Pdf.Tests;

public sealed class PdfCollageTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "docdr-collage-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Turns any bytes into a solid 4x4 BGRA block — enough to bake an image object.</summary>
    private sealed class FakeDecoder : IImageDecoder
    {
        public (int Width, int Height, int Stride, byte[] Bgra) DecodeToBgra(byte[] encoded)
        {
            const int w = 4, h = 4, stride = w * 4;
            var px = new byte[stride * h];
            Array.Fill(px, (byte)200);
            return (w, h, stride, px);
        }
    }

    [Fact]
    public void CreateBlank_makes_a_dirty_untitled_page_of_the_requested_size()
    {
        using PdfDocument doc = PdfDocument.CreateBlank(new PdfSize(595, 842));

        Assert.Equal(1, doc.PageCount);
        Assert.Null(doc.FilePath);
        Assert.True(doc.IsDirty);
        PdfSize size = doc.GetPageSize(0);
        Assert.Equal(595, size.Width, 1);
        Assert.Equal(842, size.Height, 1);

        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());
        Assert.Equal(842, reloaded.GetPageSize(0).Height, 1);
    }

    [Fact]
    public void Image_annotation_round_trips_the_bytes_and_the_box()
    {
        using PdfDocument doc = PdfDocument.CreateBlank(new PdfSize(400, 400));
        doc.ImageDecoder = new FakeDecoder();
        var png = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var box = new PdfRect(50, 300, 250, 120);
        doc.AddAnnotation(0, PdfAnnotation.NewImage(box, png, "Rob"));

        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());
        reloaded.ImageDecoder = new FakeDecoder();
        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(0));

        Assert.Equal(PdfAnnotationKind.Image, a.Kind);
        Assert.Equal(png, a.ImageData);
        Assert.Equal(50, a.Box.Left, 1);
        Assert.Equal(250, a.Box.Right, 1);
    }

    [Theory]
    [InlineData(PdfAnnotationKind.Rectangle)]
    [InlineData(PdfAnnotationKind.Ellipse)]
    public void Box_shape_round_trips(PdfAnnotationKind kind)
    {
        using PdfDocument doc = PdfDocument.CreateBlank(new PdfSize(400, 400));
        var box = new PdfRect(40, 300, 200, 100);
        doc.AddAnnotation(0, kind == PdfAnnotationKind.Ellipse
            ? PdfAnnotation.NewEllipse(box, 0xFFCC3333, "Rob")
            : PdfAnnotation.NewRectangle(box, 0xFFCC3333, "Rob"));

        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());
        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(0));
        Assert.Equal(kind, a.Kind);
        Assert.Equal(40, a.Box.Left, 1);
        Assert.Equal(200, a.Box.Right, 1);
        Assert.Equal(0xFFCC3333, a.ColorArgb); // colour round-trips via /DocDrShape
    }

    [Theory]
    [InlineData(PdfAnnotationKind.Line)]
    [InlineData(PdfAnnotationKind.Arrow)]
    public void Line_shape_round_trips_its_end_points(PdfAnnotationKind kind)
    {
        using PdfDocument doc = PdfDocument.CreateBlank(new PdfSize(400, 400));
        var from = new PdfPoint(30, 40);
        var to = new PdfPoint(300, 350);
        doc.AddAnnotation(0, kind == PdfAnnotationKind.Arrow
            ? PdfAnnotation.NewArrow(from, to, 0xFF2244AA, "Rob")
            : PdfAnnotation.NewLine(from, to, 0xFF2244AA, "Rob"));

        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());
        PdfAnnotation a = Assert.Single(reloaded.GetAnnotations(0));
        Assert.Equal(kind, a.Kind);
        Assert.Equal(2, a.Leader.Count);
        // Strokes[0] is [tip, tail] = [to, from].
        Assert.Equal(to.X, a.Leader[0].X, 1);
        Assert.Equal(from.X, a.Leader[1].X, 1);
    }

    [Fact]
    public void Blank_document_survives_a_page_rotation_and_save()
    {
        using PdfDocument doc = PdfDocument.CreateBlank(new PdfSize(300, 500));
        doc.RotatePages([0], PdfRotation.Clockwise90);

        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());
        Assert.Equal(1, reloaded.PageCount);
    }
}
