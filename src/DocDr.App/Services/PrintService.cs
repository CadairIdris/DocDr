using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DocDr.Pdf;

namespace DocDr.App.Services;

/// <summary>
/// Prints a <see cref="PdfDocument"/> in its current in-memory state — page edits and the
/// annotation overlay included — by rasterising each page through the normal PDFium pipeline
/// at print resolution and letting WPF spool it. Raster output; pages render on demand so a
/// long document does not materialise all at once.
/// </summary>
public sealed class PrintService
{
    /// <summary>Above this many pages, warn that the window will be unresponsive during the spool.</summary>
    private const int LongJobThreshold = 40;

    private readonly PageImageService _images = new(new PageRenderer());

    /// <summary>Show the print dialog and, on confirmation, spool the chosen page range.</summary>
    public void Print(PdfDocument document, string jobName)
    {
        ArgumentNullException.ThrowIfNull(document);

        var dialog = new PrintDialog
        {
            UserPageRangeEnabled = true,
            MinPage = 1,
            MaxPage = (uint)Math.Max(1, document.PageCount),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IEnumerable<int> requested = dialog.PageRangeSelection == PageRangeSelection.UserPages
            ? Enumerable.Range(dialog.PageRange.PageFrom, dialog.PageRange.PageTo - dialog.PageRange.PageFrom + 1)
            : Enumerable.Range(1, document.PageCount);

        int[] pageIndices = requested
            .Select(oneBased => oneBased - 1)
            .Where(i => i >= 0 && i < document.PageCount)
            .ToArray();

        if (pageIndices.Length == 0)
        {
            return;
        }

        if (pageIndices.Length > LongJobThreshold &&
            MessageBox.Show(
                $"Printing {pageIndices.Length} pages may take a while, and DocDr will be unresponsive until it finishes. Continue?",
                "DocDr", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            return;
        }

        var paginator = new PdfPagePaginator(
            document, _images, pageIndices,
            new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight));

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            dialog.PrintDocument(paginator, jobName);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}

/// <summary>
/// Rasterises one PDF page per <see cref="GetPage"/> call — via PDFium at <see cref="RenderDpi"/>,
/// with the document's managed annotations baked on — fitted and centred on the sheet.
/// </summary>
public sealed class PdfPagePaginator : DocumentPaginator
{
    private const double RenderDpi = 200.0;

    private readonly PdfDocument _document;
    private readonly PageImageService _images;
    private readonly IReadOnlyList<int> _pages;
    private Size _sheetSize;

    public PdfPagePaginator(PdfDocument document, PageImageService images, IReadOnlyList<int> pages, Size sheetSize)
    {
        _document = document;
        _images = images;
        _pages = pages;
        _sheetSize = sheetSize;
    }

    public override bool IsPageCountValid => true;

    public override int PageCount => _pages.Count;

    public override Size PageSize
    {
        get => _sheetSize;
        set => _sheetSize = value;
    }

    public override IDocumentPaginatorSource? Source => null;

    public override DocumentPage GetPage(int pageNumber)
    {
        int pageIndex = _pages[pageNumber];

        PdfSize points = _document.GetPageSize(pageIndex);
        double pageWidthDip = points.Width * PdfCoordinates.PointToDip;
        double pageHeightDip = points.Height * PdfCoordinates.PointToDip;

        double scale = Math.Min(_sheetSize.Width / pageWidthDip, _sheetSize.Height / pageHeightDip);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1;
        }

        double drawWidth = pageWidthDip * scale;
        double drawHeight = pageHeightDip * scale;

        int pixelWidth = Math.Max(1, (int)Math.Round(drawWidth * RenderDpi / PdfCoordinates.DipPerInch));
        int pixelHeight = Math.Max(1, (int)Math.Round(drawHeight * RenderDpi / PdfCoordinates.DipPerInch));

        ImageSource image = _document.WithAnnotationsBaked(
            () => _images.Render(_document, pageIndex, pixelWidth, pixelHeight, CancellationToken.None));

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            double offsetX = Math.Max(0, (_sheetSize.Width - drawWidth) / 2);
            double offsetY = Math.Max(0, (_sheetSize.Height - drawHeight) / 2);
            dc.DrawImage(image, new Rect(offsetX, offsetY, drawWidth, drawHeight));
        }

        return new DocumentPage(visual, _sheetSize, new Rect(_sheetSize), new Rect(_sheetSize));
    }
}
