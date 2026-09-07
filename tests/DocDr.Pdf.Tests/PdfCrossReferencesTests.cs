using System.Collections.Generic;

namespace DocDr.Pdf.Tests;

public sealed class PdfCrossReferencesTests
{
    private static PdfDocument Doc(TempWorkspace ws, params string[] lines) =>
        PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("ref.pdf"), lines));

    private static string[] Pad(string first) =>
        [first, "b", "c", "d", "e", "f", "g", "h"];

    [Fact]
    public void Scan_resolves_a_clause_cross_reference_to_its_page()
    {
        using var ws = new TempWorkspace();
        using var doc = Doc(ws, Pad("The layout shall be in accordance with 6.4 of this Standard"));
        var map = new Dictionary<string, int> { ["6"] = 3, ["6.4"] = 5 };

        PdfCrossRef reference = Assert.Single(PdfCrossReferences.Scan(doc, 0, map));

        Assert.Equal(5, reference.TargetPageIndex);
        Assert.Equal("Clause 6.4", reference.Label);
        Assert.True(reference.Rect.Width > 0 && reference.Rect.Height > 0);
    }

    [Fact]
    public void Scan_falls_back_to_the_parent_clause_page()
    {
        using var ws = new TempWorkspace();
        using var doc = Doc(ws, Pad("as defined in 6.4.9 the cover applies"));
        var map = new Dictionary<string, int> { ["6.4"] = 5 };

        PdfCrossRef reference = Assert.Single(PdfCrossReferences.Scan(doc, 0, map));
        Assert.Equal(5, reference.TargetPageIndex);
    }

    [Fact]
    public void Scan_resolves_an_annex_reference()
    {
        using var ws = new TempWorkspace();
        using var doc = Doc(ws, Pad("the method is given in Annex L of this Standard"));
        var map = new Dictionary<string, int> { ["L"] = 6 };

        PdfCrossRef reference = Assert.Single(PdfCrossReferences.Scan(doc, 0, map));
        Assert.Equal(6, reference.TargetPageIndex);
        Assert.Equal("Annex L", reference.Label);
    }

    [Fact]
    public void Scan_resolves_figure_and_table_references_against_the_caption_map()
    {
        using var ws = new TempWorkspace();
        using var doc = Doc(ws, Pad("the model in Figure 8.5 and the values in Table 4.3 apply"));
        var map = new Dictionary<string, int> { ["Figure 8.5"] = 9, ["Table 4.3"] = 4 };

        var refs = PdfCrossReferences.Scan(doc, 0, map);

        Assert.Contains(refs, r => r.TargetPageIndex == 9 && r.Label == "Figure 8.5");
        Assert.Contains(refs, r => r.TargetPageIndex == 4 && r.Label == "Table 4.3");
    }

    [Fact]
    public void Scan_does_not_prefix_walk_a_figure_number()
    {
        using var ws = new TempWorkspace();
        using var doc = Doc(ws, Pad("see Figure 8.53 for the arrangement"));
        var map = new Dictionary<string, int> { ["8"] = 2, ["8.5"] = 7 };

        // No "Figure 8.53" caption — must NOT fall back to clause 8 or 8.5.
        Assert.Empty(PdfCrossReferences.Scan(doc, 0, map));
    }

    [Fact]
    public void Scan_ignores_unresolvable_and_uncued_numbers()
    {
        using var ws = new TempWorkspace();
        using var doc = Doc(ws, Pad("a stress of 6.4 MPa and see 9.9.9 elsewhere"));
        var map = new Dictionary<string, int> { ["6.4"] = 5 };

        // "6.4" here has no cue word; "9.9.9" is cued but unresolvable.
        Assert.Empty(PdfCrossReferences.Scan(doc, 0, map));
    }

    [Fact]
    public void Scan_returns_empty_without_a_clause_map()
    {
        using var ws = new TempWorkspace();
        using var doc = Doc(ws, Pad("see 6.4 for details"));

        Assert.Empty(PdfCrossReferences.Scan(doc, 0, new Dictionary<string, int>()));
    }
}
