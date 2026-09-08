using System;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>The slice of <see cref="PdfPaneViewModel"/> that the floating format toolbar
/// (<see cref="AnnotationFormatViewModel"/>) drives. Extracted so the toolbar VM can be tested
/// against a fake host without the pane's WPF plumbing.</summary>
public interface IAnnotationFormatHost
{
    /// <summary>Apply a styling edit to one annotation.</summary>
    void ApplyFormat(Guid id, Func<PdfAnnotation, PdfAnnotation> mutate);

    /// <summary>Open the colour picker seeded with <paramref name="fallback"/>; returns the picked ARGB or null.</summary>
    uint? PickCustomColor(uint fallback);

    /// <summary>Re-fit a text box / callout after its font size changed.</summary>
    void ResizeTextBoxForFont(Guid id, double fontSize);

    /// <summary>Delete the annotation with this id.</summary>
    void DeleteAnnotation(Guid id);
}
