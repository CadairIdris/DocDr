using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DocDr.Pdf.Tests;

public sealed class PdfTextCompareTests
{
    // --- the diff itself ----------------------------------------------------------------------

    private static int[] Ids(string text, Dictionary<string, int> ids) =>
        [.. text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Select(w => ids.TryGetValue(w, out int id) ? id : ids[w] = ids.Count)];

    /// <summary>Replaying the script must turn a into b, and its equal words must be a common subsequence.</summary>
    private static void AssertValid(int[] a, int[] b, List<DiffOp> ops)
    {
        var rebuilt = new List<int>();
        int ai = 0, bi = 0;
        foreach (DiffOp op in ops)
        {
            Assert.Equal(ai, op.AStart);
            Assert.Equal(bi, op.BStart);
            if (op.Kind == DiffKind.Equal)
            {
                Assert.Equal(op.ALength, op.BLength);
                Assert.Equal(a[ai..(ai + op.ALength)], b[bi..(bi + op.BLength)]);
            }

            rebuilt.AddRange(b[bi..(bi + op.BLength)]);
            ai += op.ALength;
            bi += op.BLength;
        }

        Assert.Equal(a.Length, ai);
        Assert.Equal(b, rebuilt);
    }

    [Theory]
    [InlineData("the quick brown fox jumps", "the quick brown fox jumps", 5)]
    [InlineData("the quick brown fox jumps", "the slow brown dog fox jumps", 4)]
    [InlineData("a b c d e f", "f e d c b a", 1)]
    [InlineData("", "new words only", 0)]
    [InlineData("old words only", "", 0)]
    [InlineData("x a b x c d x", "x c d x a b x", 4)]
    public void Diff_is_a_valid_edit_script_with_the_longest_common_run(string left, string right, int expectedEqual)
    {
        var ids = new Dictionary<string, int>();
        int[] a = Ids(left, ids), b = Ids(right, ids);
        List<DiffOp> ops = SequenceDiff.Diff(a, b);
        AssertValid(a, b, ops);
        Assert.Equal(expectedEqual, ops.Where(o => o.Kind == DiffKind.Equal).Sum(o => o.ALength));
    }

    [Fact]
    public void Diff_handles_a_long_document_quickly()
    {
        var rng = new System.Random(7);
        int[] a = [.. Enumerable.Range(0, 60_000).Select(_ => rng.Next(4000))];
        var b = a.ToList();
        b.RemoveRange(1000, 50);
        b.InsertRange(30_000, Enumerable.Range(0, 40).Select(i => 5000 + i));
        b[45_000] = 9999;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        List<DiffOp> ops = SequenceDiff.Diff(a, [.. b]);
        Assert.True(timer.ElapsedMilliseconds < 5000, $"took {timer.ElapsedMilliseconds} ms");
        AssertValid(a, [.. b], ops);
        Assert.InRange(ops.Where(o => o.Kind == DiffKind.Equal).Sum(o => o.ALength), 59_940, 59_950);
    }

    // --- tokens and headings ------------------------------------------------------------------

    private static readonly PdfRect LineBox = new(72, 700, 472, 690);

    private static CompareText Lines(params string[] lines) =>
        CompareText.FromLines([.. lines.Select((t, i) => (t, i / 40, new PdfRect(72, 760 - (i % 40 * 14), 472, 750 - (i % 40 * 14))))]);

    [Fact]
    public void A_word_hyphenated_across_lines_is_one_word()
    {
        CompareText t = Lines("the longitudinal rein-", "forcement shall be");
        Assert.Equal(["the", "longitudinal", "reinforcement", "shall", "be"], t.Tokens.Select(x => x.Text));
        Assert.True(t.Tokens[2].Page2 >= 0);
    }

    [Theory]
    [InlineData("6.2.4 Shear resistance of members", "6.2.4", "Shear resistance of members")]
    [InlineData("6.2.4. Shear resistance", "6.2.4", "Shear resistance")]
    [InlineData("7 Serviceability limit states", "7", "Serviceability limit states")]
    [InlineData("B.2 Creep coefficient", "B.2", "Creep coefficient")]
    [InlineData("Annex B (informative)", "B", "(informative)")]
    [InlineData("Annex N.A. National parameters", "NA", "National parameters")]
    public void Heading_lines_are_recognised(string line, string number, string title)
    {
        (string Number, string Title)? h = CompareText.HeadingLine(line);
        Assert.NotNull(h);
        Assert.Equal(number, h.Value.Number);
        Assert.Equal(title, h.Value.Title);
    }

    [Theory]
    [InlineData("1.35 Gk")]
    [InlineData("3 The design value of the shear force shall be taken from the analysis of the structure.")]
    [InlineData("A Note on the method")]
    [InlineData("2 kN/m")]
    public void Lines_that_merely_start_with_a_number_are_not_headings(string line) =>
        Assert.Null(CompareText.HeadingLine(line));

    // --- changes ------------------------------------------------------------------------------

    private static TextCompareResult Compare(CompareText a, CompareText b, TextCompareOptions? o = null) =>
        PdfTextCompare.Compare(a, b, o ?? new TextCompareOptions(), default);

    [Fact]
    public void A_changed_value_is_one_change_with_context_and_boxes()
    {
        CompareText a = Lines("6.2 Shear", "The partial factor shall be taken as 1.5 for persistent situations.");
        CompareText b = Lines("6.2 Shear", "The partial factor shall be taken as 1.4 for persistent situations.");
        TextCompareResult r = Compare(a, b);

        TextChange c = Assert.Single(r.Changes);
        Assert.Equal(TextChangeKind.Changed, c.Kind);
        Assert.Equal("1.5", c.OldText);
        Assert.Equal("1.4", c.NewText);
        Assert.Equal("6.2", c.Clause);
        Assert.Equal(
            [RedlineKind.Equal, RedlineKind.Deleted, RedlineKind.Inserted, RedlineKind.Equal],
            c.Segments.Select(s => s.Kind));
        Assert.EndsWith("taken as", c.Segments[0].Text);
        PageBox box = Assert.Single(c.OldBoxes);
        Assert.InRange(box.Box.Left, 250, 400); // "1.5" sits over half way along the line
        Assert.Equal(1 - (2.0 / (r.OldWords + r.NewWords)), r.Similarity, 3);
    }

    [Fact]
    public void Nearby_edits_merge_and_distant_ones_do_not()
    {
        CompareText a = Lines("alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu");
        CompareText b = Lines("alpha BETA gamma DELTA epsilon zeta eta theta iota kappa LAMBDA mu");
        TextCompareResult r = Compare(a, b);
        Assert.Equal(2, r.Changes.Count);
        Assert.Equal("beta delta", r.Changes[0].OldText);
        Assert.Equal("BETA DELTA", r.Changes[0].NewText);
        Assert.Equal(
            [RedlineKind.Equal, RedlineKind.Deleted, RedlineKind.Inserted, RedlineKind.Equal, RedlineKind.Deleted, RedlineKind.Inserted, RedlineKind.Equal],
            r.Changes[0].Segments.Select(x => x.Kind));
    }

    [Fact]
    public void Inserted_and_deleted_passages_are_classified_and_a_moved_one_is_paired()
    {
        string moved = "Where the reinforcement is anchored by a hook the anchorage length may be reduced.";
        string stays = "Bond conditions are good when the bars are placed in the lower part of the section and well compacted during casting.";
        CompareText a = Lines("1 Scope", "This part covers concrete.", moved, stays, "A closing remark.");
        CompareText b = Lines("1 Scope", "This part covers concrete.", stays, moved, "A closing remark.", "A new sentence is added here.");
        TextCompareResult r = Compare(a, b);

        Assert.Contains(r.Changes, c => c.Kind == TextChangeKind.Inserted && c.NewText == "A new sentence is added here.");
        TextChange[] moves = [.. r.Changes.Where(c => c.Kind == TextChangeKind.Moved)];
        Assert.Equal(2, moves.Length);
        Assert.Equal(moves[0].Index, moves[1].MovedPartner);
        Assert.Equal(moves[1].Index, moves[0].MovedPartner);
    }

    [Fact]
    public void Ligatures_quotes_and_dashes_are_not_changes()
    {
        CompareText a = Lines("the ﬁnal ‘value’ – see note");
        CompareText b = Lines("the final 'value' - see note");
        Assert.Empty(Compare(a, b).Changes);
    }

    // --- PDFs ---------------------------------------------------------------------------------

    [Fact]
    public void Two_pdfs_compare_on_the_right_pages_ignoring_their_running_header()
    {
        using var ws = new TempWorkspace();
        string[] oldPages =
        [
            "1 Scope\nThis standard applies to the design of buildings.",
            "6.2 Shear\nThe design shear resistance shall be determined by testing.",
            "6.3 Torsion\nTorsion is treated in this clause.",
            "6.4 Punching\nPunching shear is checked at the control perimeter.",
        ];
        string[] newPages =
        [
            "1 Scope\nThis standard applies to the design of buildings and bridges.",
            "6.2 Shear\nThe design shear resistance shall be determined by testing.",
            "6.3 Torsion\nTorsion is treated in this clause.",
            "6.4 Punching\nPunching shear is checked at the control perimeter.",
        ];
        using var a = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("old.pdf"), oldPages, header: "EN 1992-1-1:2004 (E)"));
        using var b = PdfDocument.Load(TestPdfBuilder.WritePdf(ws.Path("new.pdf"), newPages, header: "EN 1992-1-1:2023 (E)"));

        TextCompareResult r = PdfTextCompare.Compare(a, b);
        TextChange c = Assert.Single(r.Changes);
        Assert.Equal(TextChangeKind.Changed, c.Kind); // "buildings." → "buildings and bridges."
        Assert.Equal(0, c.OldPage);
        Assert.Equal(0, c.NewPage);
        Assert.Equal("1", c.Clause);
        Assert.NotEmpty(c.NewBoxes);

        string report = ws.Path("changes.html");
        PdfTextCompare.WriteReport(r, "old.pdf", "new.pdf", report);
        string html = File.ReadAllText(report);
        Assert.Contains("<ins>", html);
        Assert.Contains("1 changes", html);
    }

    [Fact]
    public void A_changed_word_maps_onto_the_comparison_image()
    {
        using var ws = new TempWorkspace();
        using var a = PdfDocument.Load(TestPdfBuilder.WriteRuns(ws.Path("a.pdf"), [new("Column size 305x305 at grid line C", 72, 700, 10)]));
        using var b = PdfDocument.Load(TestPdfBuilder.WriteRuns(ws.Path("b.pdf"), [new("Column size 356x368 at grid line C", 72, 700, 10)]));
        TextChange c = Assert.Single(PdfTextCompare.Compare(a, b).Changes);
        VisualDiff d = PdfVisualCompare.ComparePages(a, 0, b, 0);

        DeviceRect box = d.MapNew(c.NewBoxes[0].Box)!.Value;
        Assert.NotEmpty(d.Regions);
        Assert.All(d.Regions, region =>
        {
            Assert.True(box.X < region.X + region.Width && region.X < box.X + box.Width, $"{box} vs {region}");
            Assert.True(box.Y < region.Y + region.Height && region.Y < box.Y + box.Height, $"{box} vs {region}");
        });
    }
}
