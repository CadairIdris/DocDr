using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DocDr.Pdf;
using Microsoft.Win32;

namespace DocDr.App.ViewModels;

/// <summary>Tools ▾ "Export HTML…": the document as structured HTML for an agent to read —
/// cleaned text plus each equation, table and figure as an image cropped from the page
/// (<see cref="PdfHtmlExporter"/>).</summary>
public sealed partial class DocumentTabViewModel
{
    [RelayCommand]
    private async Task ExportHtml()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export HTML",
            Filter = "HTML (*.html)|*.html",
            FileName = (Document.FilePath is { } p ? Path.GetFileNameWithoutExtension(p) : _baseTitle) + ".html",
            InitialDirectory = Document.FilePath is { } dir ? Path.GetDirectoryName(dir) : null,
            AddExtension = true,
            DefaultExt = ".html",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FileName;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            HtmlExportResult r = await Task.Run(() => PdfHtmlExporter.Export(Document, path));

            string summary = $"Exported {Path.GetFileName(path)}"
                + (r.ImageFolder is { } folder ? $" with its images in {Path.GetFileName(folder)}" : string.Empty) + ".\n\n"
                + $"{r.Headings:N0} headings, {r.Paragraphs:N0} paragraphs, {r.Equations:N0} equations, "
                + $"{r.Tables:N0} tables and {r.Figures:N0} figures ({r.Images:N0} images cropped from the pages).";
            if (r.EquationsFlagged > 0)
            {
                summary += $"\n\n{r.EquationsFlagged:N0} equation(s) are stacked or garbled in the text layer — their images are the "
                    + "reliable version (marked data-quality=\"suspect\").";
            }

            if (r.SuspectParagraphs > 0)
            {
                summary += $"\n\n{r.SuspectParagraphs:N0} paragraph(s) contain characters the PDF doesn't map to real symbols "
                    + "(marked data-quality=\"suspect\").";
            }

            if (r.PagesWithoutText.Count > 0)
            {
                summary += $"\n\n{r.PagesWithoutText.Count} page(s) had no text layer and were skipped — run OCR first.";
            }

            MessageBox.Show(summary, "DocDr", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not export HTML: {ex.Message}", "DocDr", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }
}
