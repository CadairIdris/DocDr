using DocDr.App.ViewModels;

namespace DocDr.App.Tests;

public sealed class AnnotationColorsTests
{
    [Theory]
    [InlineData("Yellow", 0xFFFFEB3Bu)]
    [InlineData("Green", 0xFF81C784u)]
    [InlineData("Blue", 0xFF64B5F6u)]
    [InlineData("Pink", 0xFFF06292u)]
    [InlineData("Orange", 0xFFFFB74Du)]
    public void ToArgb_maps_each_preset_key(string key, uint expected) =>
        Assert.Equal(expected, AnnotationColors.ToArgb(key));

    [Theory]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void ToArgb_falls_back_to_yellow_for_unknown_keys(string? key) =>
        Assert.Equal(0xFFFFEB3Bu, AnnotationColors.ToArgb(key));

    [Fact]
    public void ToArgb_with_custom_argb_uses_it_only_for_the_custom_key()
    {
        Assert.Equal(0xFF010203u, AnnotationColors.ToArgb(AnnotationColors.Custom, 0xFF010203u));
        Assert.Equal(0xFF81C784u, AnnotationColors.ToArgb("Green", 0xFF010203u));
    }

    [Fact]
    public void Keys_is_the_presets_plus_custom()
    {
        Assert.Equal([.. AnnotationColors.PresetKeys, AnnotationColors.Custom], AnnotationColors.Keys);
        Assert.DoesNotContain(AnnotationColors.Custom, AnnotationColors.PresetKeys);
    }

    [Fact]
    public void ExactKey_returns_the_preset_on_an_exact_rgb_match_else_custom()
    {
        Assert.Equal("Blue", AnnotationColors.ExactKey(0xFF64B5F6u));
        Assert.Equal("Blue", AnnotationColors.ExactKey(0x0064B5F6u)); // alpha ignored
        Assert.Equal(AnnotationColors.Custom, AnnotationColors.ExactKey(0xFF123456u));
    }

    [Fact]
    public void FromArgb_snaps_to_the_nearest_preset_and_never_returns_custom()
    {
        Assert.Equal("Green", AnnotationColors.FromArgb(0xFF80C880u));
        foreach (uint argb in new[] { 0xFF000000u, 0xFFFFFFFFu, 0xFF123456u })
        {
            Assert.NotEqual(AnnotationColors.Custom, AnnotationColors.FromArgb(argb));
        }
    }
}
