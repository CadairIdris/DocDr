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
