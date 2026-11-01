using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DocDr.Pdf.Tests;

public sealed class PdfClauseCompareTests
{
    private static CompareText Lines(params string[] lines) =>
        CompareText.FromLines([.. lines.Select((t, i) => (t, i / 30, new PdfRect(72, 760 - (i % 30 * 14), 472, 750 - (i % 30 * 14))))]);

    private static ClauseCompareResult Compare(CompareText a, CompareText b) =>
        PdfClauseCompare.Compare(
            PdfClauseCompare.Segment(a, new HashSet<string>()),
            PdfClauseCompare.Segment(b, new HashSet<string>()),
            default);

    private const string Materials =
        "The characteristic compressive strength of concrete is determined from cylinder tests at 28 days and classified by strength class.";

    private const string Durability =
        "The concrete cover to reinforcement shall be sufficient to protect it from corrosion for the design working life of the structure.";

    [Fact]
    public void Clauses_are_matched_by_number_then_followed_when_renumbered()
    {
        CompareText a = Lines(
            "Foreword text for the first edition.",
            "1.1 Scope", "This part applies to concrete structures.",
            "1.2 Terms", "For the purposes of this part the terms below apply.",
            "1.3 Materials", Materials,
            "1.4 Withdrawn rules", "These obsolete simplified rules apply to unbraced frames only and are no longer recommended at all.");
        CompareText b = Lines(
            "Foreword text for the second edition.",
            "1.1 Scope", "This part applies to concrete structures.",
            "1.2 Terms", "For the purposes of this document the terms below apply.",
            "1.3 Durability", Durability,
            "1.4 Materials", Materials);

        ClauseCompareResult r = Compare(a, b);
        string Row(ClauseComparison c) => $"{c.Kind}:{c.OldNumber}>{c.NewNumber}";
        Assert.Equal(
            [
                "Modified:>",            // front matter: "first" → "second"
                "Unchanged:1.1>1.1",
                "Modified:1.2>1.2",
                "Added:>1.3",
                "Renumbered:1.3>1.4",
                "Removed:1.4>",
            ],
            r.Clauses.Select(Row));

        ClauseComparison terms = r.Clauses[2];
        Assert.Contains(terms.Redline, s => s.Kind == RedlineKind.Deleted && s.Text == "part");
        Assert.Contains(terms.Redline, s => s.Kind == RedlineKind.Inserted && s.Text == "document");
        Assert.Equal("1.3 → 1.4", PdfClauseCompare.NumberLabel(r.Clauses[4]));
        Assert.Equal("Renumbered", PdfClauseCompare.KindLabel(r.Clauses[4]));
        Assert.Equal(2, r.Clauses[4].Level);
    }

    [Fact]
    public void A_title_change_alone_is_a_modification()
    {
        CompareText a = Lines("4.2 Cover", Durability);
        CompareText b = Lines("4.2 Concrete cover", Durability);
        ClauseComparison c = Compare(a, b).Clauses.Single(x => x.Number == "4.2");
        Assert.Equal(ClauseChangeKind.Modified, c.Kind);
        Assert.True(c.TitleChanged);
        Assert.Equal(1, c.Similarity);
    }

    [Fact]
    public void Known_clause_numbers_filter_out_list_items()
    {
        CompareText t = Lines("6.2 Shear", "Text.", "6.2.1 Members not requiring reinforcement", "1.5 Partial factor for steel", "More text.");
        List<PdfClauseCompare.Clause> clauses = PdfClauseCompare.Segment(t, new HashSet<string> { "6", "6.2", "6.2.1" });
        Assert.Equal(["6.2", "6.2.1"], clauses.Select(c => c.Number));
        Assert.Equal("Partial factor for steel More text.", t.Join(clauses[1].BodyStart, clauses[1].BodyEnd).Replace("1.5 ", ""));
    }

    [Fact]
    public void Two_editions_compare_clause_by_clause_and_report()
    {
        using var ws = new TempWorkspace();
        string[] oldPages =
        [
            "1 General\n1.1 Scope\nThis standard applies to the design of buildings.",
            "2 Basis of design\n2.1 Requirements\nA structure shall be designed to have adequate resistance.",
        ];
        string[] newPages =
        [
            "1 General\n1.1 Scope\nThis standard applies to the design of buildings and bridges.",
            "2 Basis of design\n2.1 Requirements\nA structure shall be designed to have adequate resistance.\n2.2 Robustness\nA structure shall not be damaged disproportionately.",
        ];
        using var a = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("old.pdf"), oldPages));
        using var b = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("new.pdf"), newPages));

        ClauseCompareResult r = PdfClauseCompare.Compare(a, b);
        Assert.Equal(ClauseChangeKind.Modified, r.Clauses.Single(c => c.Number == "1.1").Kind);
        Assert.Equal(ClauseChangeKind.Unchanged, r.Clauses.Single(c => c.Number == "2.1").Kind);
        ClauseComparison added = r.Clauses.Single(c => c.Number == "2.2");
        Assert.Equal(ClauseChangeKind.Added, added.Kind);
        Assert.Equal(1, added.NewPage);

        string report = ws.Path("clauses.html");
        PdfClauseCompare.WriteReport(r, "old.pdf", "new.pdf", report);
        string html = File.ReadAllText(report);
        Assert.Contains("2.2", html);
        Assert.Contains("Robustness", html);
        Assert.DoesNotContain(">2.1<", html); // unchanged clauses are counted, not listed
    }
}
