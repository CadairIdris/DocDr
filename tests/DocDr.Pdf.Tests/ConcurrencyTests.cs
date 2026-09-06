namespace DocDr.Pdf.Tests;

/// <summary>
/// The Stage 1 crux: two panes sharing one <see cref="PdfDocument"/>. These tests hammer the
/// shared handle from multiple threads and assert PDFium never corrupts or crashes.
/// </summary>
public sealed class ConcurrencyTests
{
    [Fact]
    public void Concurrent_renders_of_shared_document_match_single_threaded_reference()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("con.pdf"),
            Enumerable.Range(0, 12).Select(i => $"Page number {i} lorem ipsum").ToArray());
        using var doc = PdfDocument.Load(path);
        var renderer = new PageRenderer();

        byte[] reference = renderer.Render(doc, 3, 300, 388).Pixels;

        var failures = 0;
        Parallel.For(0, 200, new ParallelOptions { MaxDegreeOfParallelism = 4 }, _ =>
        {
            byte[] pixels = renderer.Render(doc, 3, 300, 388).Pixels;
            if (!pixels.AsSpan().SequenceEqual(reference))
            {
                Interlocked.Increment(ref failures);
            }
        });

        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task Interleaved_render_and_search_on_shared_document_stay_correct()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WritePdf(ws.Path("mix.pdf"),
        [
            "target one", "filler", "target two", "filler", "target three",
        ]);
        using var doc = PdfDocument.Load(path);
        var renderer = new PageRenderer();
        var search = new PdfSearch(doc);

        var renderLoop = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                _ = renderer.Render(doc, i % doc.PageCount, 220, 285);
            }
        });

        var searchLoop = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                Assert.Equal(3, search.FindAll("target").Count);
            }
        });

        await Task.WhenAll(renderLoop, searchLoop).WaitAsync(TimeSpan.FromSeconds(60));
    }
}
