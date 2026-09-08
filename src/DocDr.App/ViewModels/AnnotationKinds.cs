using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>Friendly, user-facing names for the annotation kinds — one source for the nav
/// panel rows and the selected-annotation delete affordance.</summary>
public static class AnnotationKinds
{
    public static string Label(PdfAnnotationKind kind) => kind switch
    {
        PdfAnnotationKind.Highlight => "Highlight",
        PdfAnnotationKind.Comment => "Note",
        PdfAnnotationKind.Ink => "Freehand drawing",
        PdfAnnotationKind.Cloud => "Revision cloud",
        PdfAnnotationKind.TextBox => "Text box",
        PdfAnnotationKind.Callout => "Callout",
        PdfAnnotationKind.Image => "Image",
        PdfAnnotationKind.Rectangle => "Rectangle",
        PdfAnnotationKind.Ellipse => "Ellipse",
        PdfAnnotationKind.Line => "Line",
        PdfAnnotationKind.Arrow => "Arrow",
        _ => "Annotation",
    };
}
