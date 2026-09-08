using DocDr.Pdf;

namespace DocDr.Pdf.Tests;

public sealed class PdfTrimTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "docdr-trim-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A 612x792 page whose only content is a short line around (250, 400).</summary>
    private string SmallContentPage() => TestPdfBuilder.WriteRuns(
        Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf"),
        [new TestPdfBuilder.Run("Content island", 250, 400, 14)]);

    [Fact]
    public void Trim_shrinks_the_page_to_its_content_plus_margin()
    {
        using PdfDocument doc = PdfDocument.Load(SmallContentPage());
        PdfSize before = doc.GetPageSize(0);

        doc.TrimMargins([0], marginPt: 6);

        PdfSize after = doc.GetPageSize(0);
        Assert.True(after.Width < before.Width * 0.6, $"width {after.Width} not much smaller than {before.Width}");
        Assert.True(after.Height < before.Height * 0.6, $"height {after.Height} not much smaller than {before.Height}");
        Assert.True(after.Width > 20 && after.Height > 10);
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void Trimmed_crop_survives_save_and_reload()
    {
        using PdfDocument doc = PdfDocument.Load(SmallContentPage());
        doc.TrimMargins([0], marginPt: 6);
        PdfSize trimmed = doc.GetPageSize(0);

        using PdfDocument reloaded = PdfDocument.Load(doc.SaveToBytes());
        PdfSize reloadedSize = reloaded.GetPageSize(0);
        Assert.Equal(trimmed.Width, reloadedSize.Width, 2);
        Assert.Equal(trimmed.Height, reloadedSize.Height, 2);
    }

    [Fact]
    public void Trimmed_crop_survives_a_later_rotation()
    {
        using PdfDocument doc = PdfDocument.Load(SmallContentPage());
        doc.TrimMargins([0], marginPt: 6);
        double trimmedShortEdge = System.Math.Min(doc.GetPageSize(0).Width, doc.GetPageSize(0).Height);

        doc.RotatePages([0], PdfRotation.Clockwise90); // forces a Rebuild
        PdfSize rotated = doc.GetPageSize(0);
        Assert.Equal(trimmedShortEdge, System.Math.Min(rotated.Width, rotated.Height), 2);
    }

    [Fact]
    public void Undo_restores_the_original_page_size()
    {
        using PdfDocument doc = PdfDocument.Load(SmallContentPage());
        PdfSize before = doc.GetPageSize(0);
        doc.TrimMargins([0], marginPt: 6);
        doc.Undo();

        PdfSize restored = doc.GetPageSize(0);
        Assert.Equal(before.Width, restored.Width, 1);
        Assert.Equal(before.Height, restored.Height, 1);
    }
}
