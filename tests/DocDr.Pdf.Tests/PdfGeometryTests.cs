namespace DocDr.Pdf.Tests;

public sealed class PdfGeometryTests
{
    [Fact]
    public void Crop_origin_is_the_inset_and_page_size_is_the_cropbox()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("crop.pdf"), ["one", "two"], cropInset: 36);
        using var doc = PdfDocument.Load(path);

        PdfPoint origin = doc.GetCropOrigin(0);
        Assert.Equal(36, origin.X, 1);
        Assert.Equal(36, origin.Y, 1);

        PdfSize size = doc.GetPageSize(0);
        Assert.Equal(TestPdfBuilder.PageWidth - 72, size.Width, 1);
        Assert.Equal(TestPdfBuilder.PageHeight - 72, size.Height, 1);
    }

    [Fact]
    public void No_cropbox_means_no_offset()
    {
        using var ws = new TempWorkspace();
        using var doc = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("plain.pdf"), ["a"]));

        PdfPoint origin = doc.GetCropOrigin(0);
        Assert.Equal(0, origin.X, 3);
        Assert.Equal(0, origin.Y, 3);
    }

    [Theory]
    [InlineData(PdfRotation.None)]
    [InlineData(PdfRotation.Clockwise90)]
    [InlineData(PdfRotation.Rotate180)]
    [InlineData(PdfRotation.CounterClockwise90)]
    public void Device_page_round_trip_survives_a_crop_origin(PdfRotation rotation)
    {
        var unrotated = new PdfSize(432, 648);   // a CropBox-sized page
        var crop = new PdfPoint(36, 30);
        const double scale = 1.5;

        // a point in MediaBox space that sits inside the crop region
        var pagePoint = new PdfPoint(120 + crop.X, 400 + crop.Y);

        (double dx, double dy) = PdfCoordinates.PageToDevicePoint(pagePoint, unrotated, rotation, scale, crop);
        PdfPoint back = PdfCoordinates.DeviceToPage(dx, dy, unrotated, rotation, scale, crop);

        Assert.Equal(pagePoint.X, back.X, 3);
        Assert.Equal(pagePoint.Y, back.Y, 3);
    }
}
