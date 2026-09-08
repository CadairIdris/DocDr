using DocDr.App.Services;

namespace DocDr.App.Tests;

public sealed class PageSizesTests
{
    private const double PtPerMm = 72.0 / 25.4;

    [Fact]
    public void FromMm_converts_millimetres_to_points()
    {
        var a4 = PageSizes.FromMm(210, 297, landscape: false);
        Assert.Equal(210 * PtPerMm, a4.Width, 3);
        Assert.Equal(297 * PtPerMm, a4.Height, 3);
    }

    [Fact]
    public void FromMm_swaps_the_axes_for_landscape()
    {
        var land = PageSizes.FromMm(210, 297, landscape: true);
        Assert.True(land.Width > land.Height);
        Assert.Equal(297 * PtPerMm, land.Width, 3);
    }

    [Fact]
    public void FromMm_leaves_an_already_wide_page_alone_when_landscape_is_asked()
    {
        var wide = PageSizes.FromMm(297, 210, landscape: true);
        Assert.Equal(297 * PtPerMm, wide.Width, 3);
        Assert.Equal(210 * PtPerMm, wide.Height, 3);
    }

    [Theory]
    [InlineData("A4", 210, 297)]
    [InlineData("a3", 297, 420)]
    [InlineData("A1", 594, 841)]
    public void StandardMm_looks_up_by_name_case_insensitively(string name, double w, double h)
    {
        var mm = PageSizes.StandardMm(name);
        Assert.NotNull(mm);
        Assert.Equal((w, h), (mm!.Value.WidthMm, mm.Value.HeightMm));
    }

    [Fact]
    public void StandardMm_returns_null_for_an_unknown_name() =>
        Assert.Null(PageSizes.StandardMm("Custom"));
}
