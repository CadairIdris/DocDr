namespace DocDr.Pdf.Tests;

public sealed class PdfTextWrapTests
{
    [Fact]
    public void Wrap_breaks_at_word_boundaries_to_stay_within_width()
    {
        IReadOnlyList<string> lines = PdfTextWrap.Wrap(
            "The quick brown fox jumps over the lazy dog", maxWidth: 90, fontSize: 11);

        Assert.True(lines.Count > 1);
        foreach (string line in lines)
        {
            Assert.DoesNotContain("  ", line);
        }

        Assert.Equal(
            "The quick brown fox jumps over the lazy dog".Replace(" ", string.Empty),
            string.Concat(lines).Replace(" ", string.Empty));
    }

    [Fact]
    public void Wrap_honours_existing_line_breaks()
    {
        IReadOnlyList<string> lines = PdfTextWrap.Wrap("one\ntwo\nthree", maxWidth: 500, fontSize: 11);
        Assert.Equal(["one", "two", "three"], lines);
    }

    [Fact]
    public void FittedSize_shrinks_to_the_text_and_never_exceeds_the_max_width()
    {
        (double wideW, double wideH) = PdfTextWrap.FittedSize("OK", maxWidth: 400, fontSize: 12);
        Assert.True(wideW < 60, "A two-letter box should be far narrower than the 400pt cap.");
        Assert.True(wideH is > 12 and < 30);

        (double para, _) = PdfTextWrap.FittedSize(
            "A longer sentence that has to wrap across a couple of lines in the box.",
            maxWidth: 150, fontSize: 12);
        Assert.True(para <= 150);
    }
}
