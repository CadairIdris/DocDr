using System.Globalization;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>How a page is named to the user: its printed <c>/PageLabels</c> label ("vii", "A-3")
/// when the document carries one, else the plain 1-based ordinal.</summary>
public static class PageDisplay
{
    public static string Label(PdfDocument document, int pageIndex) =>
        document.GetPageLabel(pageIndex) ?? Ordinal(pageIndex);

    public static string Ordinal(int pageIndex) =>
        (pageIndex + 1).ToString(CultureInfo.InvariantCulture);
}
