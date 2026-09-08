using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App.Tests;

public sealed class AnnotationKindsTests
{
    [Theory]
    [InlineData(PdfAnnotationKind.Highlight, "Highlight")]
    [InlineData(PdfAnnotationKind.Comment, "Note")]
    [InlineData(PdfAnnotationKind.Ink, "Freehand drawing")]
    [InlineData(PdfAnnotationKind.Cloud, "Revision cloud")]
    [InlineData(PdfAnnotationKind.TextBox, "Text box")]
    [InlineData(PdfAnnotationKind.Callout, "Callout")]
    [InlineData(PdfAnnotationKind.Image, "Image")]
    [InlineData(PdfAnnotationKind.Rectangle, "Rectangle")]
    [InlineData(PdfAnnotationKind.Ellipse, "Ellipse")]
    [InlineData(PdfAnnotationKind.Line, "Line")]
    [InlineData(PdfAnnotationKind.Arrow, "Arrow")]
    public void Label_names_each_kind(PdfAnnotationKind kind, string expected) =>
        Assert.Equal(expected, AnnotationKinds.Label(kind));

    [Fact]
    public void Every_declared_kind_has_a_non_empty_label()
    {
        foreach (PdfAnnotationKind kind in Enum.GetValues<PdfAnnotationKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(AnnotationKinds.Label(kind)));
        }
    }
}
