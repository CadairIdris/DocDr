using System.Collections.Generic;
using System.Linq;

namespace DocDr.Pdf.Tests;

public sealed class PdfTableExtractorTests
{
    // Builds a grid of runs: column x-origins, row y-origins (top-down), cells[row][col].
    private static List<TestPdfBuilder.Run> Grid(double[] xs, double topY, double rowGap, string[][] cells)
    {
        var runs = new List<TestPdfBuilder.Run>();
        for (int r = 0; r < cells.Length; r++)
        {
            double y = topY - (r * rowGap);
            for (int c = 0; c < cells[r].Length && c < xs.Length; c++)
            {
                if (cells[r][c].Length > 0)
                {
                    runs.Add(new TestPdfBuilder.Run(cells[r][c], xs[c], y));
                }
            }
        }

        return runs;
    }

    [Fact]
    public void Extract_recovers_a_clean_numeric_table()
    {
        using var ws = new TempWorkspace();
        string[][] cells =
        [
            ["Class", "C20", "C30", "C40"],
            ["fck", "20", "30", "40"],
            ["fctm", "2.2", "2.9", "3.5"],
            ["Ecm", "30", "33", "35"],
        ];
        string path = TestPdfBuilder.WriteRuns(
            ws.Path("t.pdf"), Grid([90, 200, 280, 360], 600, 22, cells));
        using var doc = PdfDocument.Load(path);

        TableGrid grid = PdfTableExtractor.Extract(doc, 0, new PdfRect(80, 615, 400, 520));

        Assert.Equal(4, grid.RowCount);
        Assert.Equal(4, grid.ColumnCount);
        Assert.Equal(["Class", "C20", "C30", "C40"], grid.Rows[0]);
        Assert.Equal(["fctm", "2.2", "2.9", "3.5"], grid.Rows[2]);
    }

    [Fact]
    public void Extract_keeps_a_wide_text_label_in_the_first_column()
    {
        using var ws = new TempWorkspace();
        string[][] cells =
        [
            ["Design situation", "gC", "gS"],
            ["Persistent and transient", "1.50", "1.15"],
            ["Accidental", "1.20", "1.00"],
        ];
        string path = TestPdfBuilder.WriteRuns(
            ws.Path("f.pdf"), Grid([90, 320, 380], 600, 24, cells));
        using var doc = PdfDocument.Load(path);

        TableGrid grid = PdfTableExtractor.Extract(doc, 0, new PdfRect(80, 615, 420, 515));

        Assert.Equal(3, grid.ColumnCount);
        Assert.Equal("Persistent and transient", grid.Cell(1, 0));
        Assert.Equal("1.50", grid.Cell(1, 1));
        Assert.Equal("1.00", grid.Cell(2, 2));
    }

    [Fact]
    public void Extract_uses_ruling_lines_to_split_a_sparse_narrow_first_column()
    {
        using var ws = new TempWorkspace();
        // A one-letter "Category" column beside two wide prose columns — like EN 1991-1-1 Table 6.1.
        // Whitespace voting alone folds the lone letters into the next column; the grid rules don't.
        string[][] cells =
        [
            ["Cat", "Specific use", "Example"],
            ["A", "domestic and residential activities", "houses; hospitals; hotels"],
            ["B", "office areas", ""],
            ["C", "areas where people congregate", "schools, cafes, restaurants, halls"],
        ];
        var runs = Grid([95, 132, 310], 600, 26, cells);
        var rects = new List<TestPdfBuilder.Rect>
        {
            // four vertical rules (thin filled rects, as real code PDFs draw them) + a top/bottom rule
            new(90, 490, 90.6, 612), new(126, 490, 126.6, 612),
            new(300, 490, 300.6, 612), new(470, 490, 470.6, 612),
            new(90, 611.4, 470, 612), new(90, 490, 470, 490.6),
        };
        string path = TestPdfBuilder.WriteRuns(ws.Path("ruled.pdf"), runs, rects);
        using var doc = PdfDocument.Load(path);

        TableGrid grid = PdfTableExtractor.Extract(doc, 0, new PdfRect(85, 616, 480, 486));

        Assert.Equal(3, grid.ColumnCount);
        Assert.Equal("A", grid.Cell(1, 0));
        Assert.Equal("domestic and residential activities", grid.Cell(1, 1));
        Assert.Equal("schools, cafes, restaurants, halls", grid.Cell(3, 2));
    }

    [Fact]
    public void Extract_drops_a_margin_watermark_a_loose_selection_caught()
    {
        using var ws = new TempWorkspace();
        var runs = Grid([120, 220, 300], 600, 26,
        [
            ["Class", "C20", "C30"],
            ["fck", "20", "30"],
            ["fctm", "2.2", "2.9"],
        ]);
        // stray single characters up the left margin, clear of the table rows (y = 600/574/548)
        foreach (double y in new[] { 509.0, 522, 535, 561, 587, 613, 626 })
        {
            runs.Add(new TestPdfBuilder.Run("X", 24, y));
        }

        string path = TestPdfBuilder.WriteRuns(ws.Path("margin.pdf"), runs);
        using var doc = PdfDocument.Load(path);

        // selection sloppily reaches into the left margin
        TableGrid grid = PdfTableExtractor.Extract(doc, 0, new PdfRect(12, 616, 340, 500));

        Assert.Equal(3, grid.ColumnCount);
        Assert.Equal(["Class", "C20", "C30"], grid.Rows[0]);
        Assert.DoesNotContain("X", grid.Rows.SelectMany(r => r));
    }

    [Fact]
    public void ToCsv_quotes_fields_that_need_it()
    {
        var grid = new TableGrid([["plain", "has, comma"], ["has \"quote\"", "x"]]);
        string csv = grid.ToCsv();

        Assert.Contains("plain,\"has, comma\"", csv);
        Assert.Contains("\"has \"\"quote\"\"\",x", csv);
        Assert.EndsWith("\r\n", csv);
    }

    [Fact]
    public void ToTsv_flattens_newlines_and_tabs()
    {
        var grid = new TableGrid([["a\tb", "c\r\nd"]]);
        Assert.Equal("a b\tc d\n", grid.ToTsv());
    }

    [Fact]
    public void Extract_of_an_empty_region_is_an_empty_grid()
    {
        using var ws = new TempWorkspace();
        string path = TestPdfBuilder.WriteRuns(ws.Path("e.pdf"), [new TestPdfBuilder.Run("far away", 400, 100)]);
        using var doc = PdfDocument.Load(path);

        TableGrid grid = PdfTableExtractor.Extract(doc, 0, new PdfRect(50, 700, 200, 600));
        Assert.Equal(0, grid.RowCount);
    }
}
