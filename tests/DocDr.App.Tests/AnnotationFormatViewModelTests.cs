using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

public sealed class AnnotationFormatViewModelTests
{
    private static readonly PdfRect Box = new(40, 300, 220, 120);

    private static (AnnotationFormatViewModel Vm, FakeFormatHost Host) ForRectangle() =>
        For(PdfAnnotation.NewRectangle(Box, 0xFF2244AA, "Rob"));

    private static (AnnotationFormatViewModel Vm, FakeFormatHost Host) For(PdfAnnotation a)
    {
        var host = new FakeFormatHost();
        return (new AnnotationFormatViewModel(host, a), host);
    }

    [Fact]
    public void Rectangle_shows_stroke_width_dashed_and_fill_but_not_text()
    {
        (AnnotationFormatViewModel vm, _) = ForRectangle();

        Assert.True(vm.ShowStrokeColor);
        Assert.True(vm.ShowLineWidth);
        Assert.True(vm.ShowDashed);
        Assert.True(vm.ShowFill);
        Assert.False(vm.ShowTextColor);
        Assert.False(vm.ShowBorderToggle);
        Assert.False(vm.ShowFontSize);
    }

    [Fact]
    public void Text_box_shows_text_colour_border_and_font_but_not_fill_or_dashed()
    {
        (AnnotationFormatViewModel vm, _) = For(PdfAnnotation.NewTextBox(Box, "hi", 0xFF000000));

        Assert.True(vm.ShowTextColor);
        Assert.True(vm.ShowBorderToggle);
        Assert.True(vm.ShowFontSize);
        Assert.False(vm.ShowFill);
        Assert.False(vm.ShowDashed);
    }

    [Fact]
    public void Note_shows_only_the_stroke_colour()
    {
        (AnnotationFormatViewModel vm, _) = For(PdfAnnotation.NewComment(Box, "note", "Rob"));

        Assert.True(vm.ShowStrokeColor);
        Assert.False(vm.ShowLineWidth);
        Assert.False(vm.ShowDashed);
        Assert.False(vm.ShowFill);
        Assert.False(vm.ShowFontSize);
    }

    [Fact]
    public void Ctor_does_not_apply_an_edit()
    {
        (_, FakeFormatHost host) = ForRectangle();
        Assert.Null(host.LastMutateId);
    }

    [Fact]
    public void PickStroke_applies_the_preset_colour()
    {
        (AnnotationFormatViewModel vm, FakeFormatHost host) = ForRectangle();

        vm.PickStrokeCommand.Execute("Green");

        Assert.Equal(0xFF81C784u, vm.StrokeColorArgb);
        Assert.Equal(0xFF81C784u, host.Apply(PdfAnnotation.NewRectangle(Box, 0xFF2244AA)).ColorArgb);
    }

    [Fact]
    public void PickStroke_custom_routes_through_the_host_picker()
    {
        (AnnotationFormatViewModel vm, FakeFormatHost host) = ForRectangle();
        host.CustomColor = 0xFFABCDEF;

        vm.PickStrokeCommand.Execute(AnnotationColors.Custom);

        Assert.Equal(0xFFABCDEFu, vm.StrokeColorArgb);
    }

    [Fact]
    public void Toggling_dashed_and_width_applies_the_matching_edit()
    {
        (AnnotationFormatViewModel vm, FakeFormatHost host) = ForRectangle();

        vm.Dashed = true;
        Assert.True(host.Apply(PdfAnnotation.NewRectangle(Box, 0xFF2244AA)).Dashed);

        vm.LineWidth = 3;
        Assert.Equal(3, host.Apply(PdfAnnotation.NewRectangle(Box, 0xFF2244AA)).LineWidth);
    }

    [Fact]
    public void Border_toggle_maps_to_borderless()
    {
        (AnnotationFormatViewModel vm, FakeFormatHost host) = For(PdfAnnotation.NewTextBox(Box, "hi", 0xFF000000));

        vm.HasBorder = false;

        Assert.True(host.Apply(PdfAnnotation.NewTextBox(Box, "hi", 0xFF000000)).Borderless);
    }

    [Fact]
    public void Match_text_to_outline_sets_the_text_colour_from_the_stroke()
    {
        (AnnotationFormatViewModel vm, FakeFormatHost host) = For(PdfAnnotation.NewTextBox(Box, "hi", 0xFF2244AA));

        vm.MatchTextToOutline = true;

        Assert.Equal(vm.StrokeColorArgb, host.Apply(PdfAnnotation.NewTextBox(Box, "hi", 0xFF2244AA)).TextColorArgb);
    }

    [Fact]
    public void StepFontSize_clamps_and_notifies_the_host()
    {
        (AnnotationFormatViewModel vm, FakeFormatHost host) = For(PdfAnnotation.NewTextBox(Box, "hi", 0xFF000000, fontSize: 10));

        vm.StepFontSizeCommand.Execute("+");

        Assert.Equal(11, vm.FontSize);
        Assert.Equal(11, host.LastResizeFontSize);
    }

    [Fact]
    public void Delete_asks_the_host_to_delete_this_annotation()
    {
        PdfAnnotation a = PdfAnnotation.NewRectangle(Box, 0xFF2244AA);
        (AnnotationFormatViewModel vm, FakeFormatHost host) = For(a);

        vm.DeleteCommand.Execute(null);

        Assert.Equal(a.Id, host.DeletedId);
    }
}
