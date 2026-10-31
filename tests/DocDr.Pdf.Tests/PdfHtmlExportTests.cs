using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace DocDr.Pdf.Tests;

public sealed class PdfHtmlExportTests
{
    // --- PNG ----------------------------------------------------------------------------------

    private static byte[] Bgrx(int w, int h, System.Func<int, int, (byte B, byte G, byte R)> pixel)
    {
        var buf = new byte[w * 4 * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (byte b, byte g, byte r) = pixel(x, y);
                int o = (y * w * 4) + (x * 4);
                (buf[o], buf[o + 1], buf[o + 2], buf[o + 3]) = (b, g, r, 255);
            }
        }

        return buf;
    }

    /// <summary>The PNG's chunks (type → data), checking each CRC on the way.</summary>
    private static Dictionary<string, byte[]> Chunks(byte[] png)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        var chunks = new Dictionary<string, byte[]>();
        int o = 8;
        while (o < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(o));
            string type = System.Text.Encoding.ASCII.GetString(png, o + 4, 4);
            byte[] data = png[(o + 8)..(o + 8 + len)];
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(o + 8 + len));
            Assert.Equal(PngWriter.Crc(0xFFFFFFFFu, png.AsSpan(o + 4, 4 + len)) ^ 0xFFFFFFFFu, crc);
            chunks[type] = data;
            o += 12 + len;
        }

        return chunks;
    }

    [Theory]
    [InlineData(false, 0, 1)] // black-and-white page art → greyscale, 1 byte a pixel
    [InlineData(true, 2, 3)]  // any colour → RGB
    public void Png_is_well_formed_and_greyscale_when_it_can_be(bool colour, byte colourType, int channels)
    {
        const int w = 5, h = 3;
        byte[] png = PngWriter.EncodeBgrx(w, h, w * 4,
            Bgrx(w, h, (x, y) => colour && x == 2 ? ((byte)0, (byte)0, (byte)255) : ((byte)(x * 40), (byte)(x * 40), (byte)(x * 40))));

        Dictionary<string, byte[]> chunks = Chunks(png);
        Assert.Equal(w, BinaryPrimitives.ReadInt32BigEndian(chunks["IHDR"]));
        Assert.Equal(h, BinaryPrimitives.ReadInt32BigEndian(chunks["IHDR"].AsSpan(4)));
        Assert.Equal(colourType, chunks["IHDR"][9]);
        Assert.True(chunks.ContainsKey("IEND"));

        using var z = new ZLibStream(new MemoryStream(chunks["IDAT"]), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        Assert.Equal(h * (1 + (w * channels)), raw.Length);
    }

    // --- Region detection ---------------------------------------------------------------------

    private static PdfChunkText.RawChar C(int unit, double x, double y) => new(unit, new PdfRect(x, y + 8, x + 5, y));

    private static IEnumerable<PdfChunkText.RawChar> Line(string s, double x, double y) =>
        s.Select((ch, k) => C(ch, x + (k * 6), y)).Append(C('\r', 0, y)).Append(C('\n', 0, y));

    private static ChunkPage Page(params IEnumerable<PdfChunkText.RawChar>[] lines) =>
        PdfChunkText.BuildPage([.. lines.SelectMany(l => l)], 0, 792, left: 0, right: 612);

    [Fact]
    public void A_ruled_table_under_its_caption_is_one_region_holding_its_cells()
    {
        ChunkPage page = Page(
            Line("Table 3.1 - Strength classes", 72, 700),
            Line("C20/25   C30/37", 80, 670),
            Line("20       30", 80, 640),
            Line("The values above apply to normal weight concrete in this document.", 72, 560));
        PdfRect[] rules =
        [
            new(72, 690, 400, 689), new(72, 660, 400, 659), new(72, 630, 400, 629), // horizontal
            new(72, 690, 73, 629), new(200, 690, 201, 629), new(399, 690, 400, 629), // vertical
        ];

        PageRegion table = Assert.Single(PdfPageRegions.Detect(page, rules));

        Assert.Equal(PageRegionKind.Table, table.Kind);
        Assert.Equal("Table 3.1", table.Label);
        Assert.Equal(0, table.CaptionLine);
        Assert.InRange(table.Bounds.Top, 689, 700);
        Assert.InRange(table.Bounds.Bottom, 620, 630);
        Assert.True(table.Bounds.Bottom > 570); // the paragraph after it isn't swallowed
    }

    [Fact]
    public void A_figure_reaches_up_past_its_key_to_the_drawing()
    {
        ChunkPage page = Page(
            Line("Key", 72, 380),
            Line("1 slab edge", 72, 368),
            Line("2 column", 72, 356),
            Line("Figure 8.21 - Eccentricity at edge columns", 120, 330));
        PdfRect[] drawing = [new(100, 600, 400, 420), new(150, 560, 250, 500)];

        PageRegion figure = Assert.Single(PdfPageRegions.Detect(page, drawing));

        Assert.Equal(PageRegionKind.Figure, figure.Kind);
        Assert.InRange(figure.Bounds.Top, 599, 601);
        Assert.InRange(figure.Bounds.Bottom, 350, 360); // the key's last line, not the caption
    }

    [Fact]
    public void Prose_between_a_drawing_and_a_caption_stops_the_figure_reaching_for_it()
    {
        ChunkPage page = Page(
            Line("The design value should be determined from the expression given below.", 72, 380),
            Line("Figure 8.21 - Eccentricity at edge columns", 120, 330));

        Assert.Empty(PdfPageRegions.Detect(page, [new PdfRect(100, 600, 400, 420)]));
    }

    [Fact]
    public void A_wrapped_caption_takes_its_continuation_line()
    {
        ChunkPage page = Page(
            Line("Figure O.1 - First order moments in a", 120, 330),
            Line("braced column", 120, 320));

        PageRegion figure = Assert.Single(PdfPageRegions.Detect(page, [new PdfRect(100, 600, 400, 345)]));
        Assert.Equal("Figure O.1 - First order moments in a braced column", figure.Caption);
        Assert.Equal([1], figure.CaptionContinuation);
    }

    // --- Whole export -------------------------------------------------------------------------

    private static string WriteSample(TempWorkspace ws)
    {
        var runs = new List<TestPdfBuilder.Run>
        {
            new("3.1 Concrete strength", 72, 720, 12),
            new("The characteristic strength is given in Table 3.1 for each class.", 72, 700, 9),
            new("Table 3.1 - Strength classes for concrete", 72, 670, 9),
            new("C20/25", 90, 645, 9), new("C30/37", 230, 645, 9),
            new("20", 90, 620, 9), new("30", 230, 620, 9),
            new("The design compressive strength is defined as follows.", 72, 560, 9),
            new("fcd = acc fck / gc", 120, 530, 9), new("(3.15)", 520, 530, 9),
            new("Licensed copy: Joe Bloggs, Acme Ltd, Version correct as of 01/01/2026", 72, 300, 7),
        };
        TestPdfBuilder.Rect[] rules =
        [
            new(72, 660, 400, 660.8), new(72, 636, 400, 636.8), new(72, 612, 400, 612.8),
            new(72, 612, 72.8, 660.8), new(210, 612, 210.8, 660.8), new(399.2, 612, 400, 660.8),
        ];
        return TestPdfBuilder.WriteRuns(ws.Path("sample.pdf"), runs, rules);
    }

    [Fact]
    public void Export_writes_headings_text_and_image_crops_for_tables_and_equations()
    {
        using var ws = new TempWorkspace();
        using var doc = PdfDocument.Load(WriteSample(ws));
        string htmlPath = ws.Path("sample.html");

        HtmlExportResult result = PdfHtmlExporter.Export(doc, htmlPath);
        string html = File.ReadAllText(htmlPath);

        Assert.Equal(1, result.Tables);
        Assert.Equal(1, result.Equations);
        Assert.Equal(2, result.Images);
        Assert.Contains("<h3 id=\"sec-3.1\"", html);
        Assert.Contains("id=\"table-3.1\"", html);
        Assert.Contains("id=\"eq-3.15\"", html);
        Assert.Contains("data-pdf-page=\"1\"", html);

        // The crops exist as real PNGs in the sidecar folder.
        string[] pngs = Directory.GetFiles(result.ImageFolder!, "*.png");
        Assert.Equal(2, pngs.Length);
        Assert.All(pngs, p => Assert.Equal(0x89, File.ReadAllBytes(p)[0]));
        Assert.Contains("src=\"sample_files/", html);

        // Table cells are inside the table figure (image + unverified grid), not loose paragraphs.
        Assert.Contains("<table data-verified=\"false\">", html);
        Assert.DoesNotMatch(new Regex("<p(\\s[^>]*)?>[^<]*C30/37"), html);
        Assert.Contains("<td>C30/37</td>", html);

        // Ordinary text survives; the licence stamp doesn't.
        Assert.Contains("The design compressive strength is defined as follows.", html);
        Assert.DoesNotContain("Joe Bloggs", html);
    }

    [Fact]
    public void Images_can_be_embedded_in_one_self_contained_file()
    {
        using var ws = new TempWorkspace();
        using var doc = PdfDocument.Load(WriteSample(ws));

        HtmlExportResult result = PdfHtmlExporter.Export(doc, ws.Path("one.html"), new HtmlExportOptions { EmbedImages = true });

        Assert.Null(result.ImageFolder);
        Assert.False(Directory.Exists(ws.Path("one_files")));
        Assert.Equal(2, Regex.Count(File.ReadAllText(ws.Path("one.html")), "src=\"data:image/png;base64,"));
    }
}
