namespace DocDr.Pdf.Tests;

public sealed class PdfClausesTests
{
    private static PdfDocument Load(TempWorkspace ws, params string[] pageLines) =>
        PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("code.pdf"), pageLines));

    [Fact]
    public void Read_builds_a_numbered_clause_tree()
    {
        using var ws = new TempWorkspace();
        using var doc = Load(
            ws,
            "5 Materials",
            "5.1 Concrete",
            "5.2 Reinforcing steel",
            "6 Durability and concrete cover",
            "6.1 General",
            "6.4 Exposure resistance classes");

        var roots = PdfClauses.Read(doc);

        Assert.Equal(["5", "6"], roots.Select(c => c.Number).ToArray());
        Assert.Equal("Materials", roots[0].Title);
        Assert.Equal(["5.1", "5.2"], roots[0].Children.Select(c => c.Number).ToArray());
        Assert.Equal("Reinforcing steel", roots[0].Children[1].Title);
        Assert.Equal(1, roots[0].Level);
        Assert.Equal(2, roots[0].Children[0].Level);
        Assert.Equal(0, roots[0].PageIndex);
        Assert.Equal(3, roots[1].PageIndex);
        Assert.Equal(["6.1", "6.4"], roots[1].Children.Select(c => c.Number).ToArray());
    }

    [Fact]
    public void Read_synthesises_a_missing_parent_clause()
    {
        using var ws = new TempWorkspace();
        using var doc = Load(ws, "Cover page", "9.1 General", "9.2 Stress limitations");

        var roots = PdfClauses.Read(doc);

        PdfClause nine = Assert.Single(roots);
        Assert.Equal("9", nine.Number);
        Assert.Equal(string.Empty, nine.Title);
        Assert.Equal(["9.1", "9.2"], nine.Children.Select(c => c.Number).ToArray());
    }

    [Fact]
    public void Read_orders_subclauses_numerically_not_lexically()
    {
        using var ws = new TempWorkspace();
        using var doc = Load(
            ws, "8 Design", "8.2 Shear", "8.10 Detailing", "8.1 Bending");

        var roots = PdfClauses.Read(doc);

        Assert.Equal(["8.1", "8.2", "8.10"], Assert.Single(roots).Children.Select(c => c.Number).ToArray());
    }

    [Fact]
    public void Read_detects_annex_clauses()
    {
        using var ws = new TempWorkspace();
        using var doc = Load(
            ws,
            "Annex A",
            "A.1 Use of this annex",
            "A.2 Scope and field of application");

        var roots = PdfClauses.Read(doc);

        PdfClause annexA = Assert.Single(roots);
        Assert.Equal("A", annexA.Number);
        Assert.Equal("Annex A", annexA.Display);
        Assert.Equal(["A.1", "A.2"], annexA.Children.Select(c => c.Number).ToArray());
        Assert.Equal("Use of this annex", annexA.Children[0].Title);
    }

    [Fact]
    public void Read_ignores_running_text_and_contents_entries()
    {
        using var ws = new TempWorkspace();
        using var doc = Load(
            ws,
            "6.1 General requirements for the design shall be in accordance with 6.2",
            "6.4 Exposure resistance classes...........................................87",
            "The value of 5.2 may be taken from Table 5.1 for this purpose");

        Assert.Empty(PdfClauses.Read(doc));
    }

    [Fact]
    public void Read_keeps_the_first_page_a_heading_appears_on()
    {
        using var ws = new TempWorkspace();
        using var doc = Load(ws, "7 Structural analysis", "7.1 General", "7 Structural analysis");

        PdfClause seven = Assert.Single(PdfClauses.Read(doc));
        Assert.Equal(0, seven.PageIndex);
    }

    [Fact]
    public void ReadStructure_collects_figure_and_table_captions()
    {
        using var ws = new TempWorkspace();
        using var doc = Load(
            ws,
            "5 Materials",
            "Figure 5.1 - Stress-strain relationship for concrete",
            "5.2 Reinforcing steel",
            "Table 5.3 : Properties of reinforcement",
            "as shown in Figure 5.1 the curve is non-linear");

        var captions = PdfClauses.ReadStructure(doc).Captions;

        Assert.Equal(1, captions["Figure 5.1"]);
        Assert.Equal(3, captions["Table 5.3"]);
        Assert.False(captions.ContainsKey("Figure 5.1 the")); // the inline mention is not a caption
    }

    [Fact]
    public void Read_returns_empty_for_a_document_without_clause_numbering()
    {
        using var ws = new TempWorkspace();
        using var doc = Load(ws, "Just some prose", "More prose here", "Nothing numbered");

        Assert.Empty(PdfClauses.Read(doc));
    }
}
