using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>
/// The collapsible page-thumbnail strip for a document tab: navigation (click a thumbnail to
/// jump the pane there) and page-management (multi-select + context menu for rotate/delete/
/// insert). Shares the document, render queue and page cache; renders only visible thumbnails.
/// </summary>
public sealed partial class ThumbnailStripViewModel : ObservableObject
{
    /// <summary>Thumbnail width in DIP. Kept small and fixed.</summary>
    public const double ThumbnailWidth = 118.0;

    private readonly PdfDocument _document;
    private readonly BackgroundRenderQueue _queue;
    private double _deviceScale = 1.0;

    public ThumbnailStripViewModel(PdfDocument document, IReadOnlyList<PdfSize> pageSizes, BackgroundRenderQueue queue)
    {
        _document = document;
        _queue = queue;
        Thumbnails = [];
        Populate(pageSizes);
    }

    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; }

    /// <summary>Page indices (0-based) selected in the strip, ascending.</summary>
    public IReadOnlyList<int> SelectedPageIndices { get; private set; } = [];

    public bool HasSelection => SelectedPageIndices.Count > 0;

    /// <summary>Raised when a single thumbnail is chosen; carries the 0-based page index.</summary>
    public event Action<int>? PageActivated;

    /// <summary>Raised when the pane's current page moves, so the view can scroll it into view.</summary>
    public event Action<int>? CurrentPageChanged;

    /// <summary>Page-edit actions available from the strip's context menu.</summary>
    public enum ThumbnailCommand
    {
        RotateRight,
        RotateLeft,
        Rotate180,
        Delete,
        InsertAfter,
    }

    /// <summary>Raised when a context-menu action is chosen; the tab performs the edit.</summary>
    public event Action<ThumbnailCommand>? EditRequested;

    [RelayCommand]
    private void ContextEdit(string command)
    {
        if (Enum.TryParse(command, out ThumbnailCommand parsed))
        {
            EditRequested?.Invoke(parsed);
        }
    }

    /// <summary>Rebuild the strip after the document's pages changed.</summary>
    public void Reload(IReadOnlyList<PdfSize> pageSizes)
    {
        Thumbnails.Clear();
        Populate(pageSizes);
        SelectedPageIndices = [];
    }

    /// <summary>The view pushes its ListBox selection here on SelectionChanged.</summary>
    public void SetSelection(IEnumerable selectedItems)
    {
        var indices = selectedItems
            .OfType<ThumbnailViewModel>()
            .Select(t => t.PageIndex)
            .OrderBy(i => i)
            .ToArray();

        SelectedPageIndices = indices;

        // A single-item selection is a navigation gesture; a multi-item selection is a page pick.
        if (indices.Length == 1)
        {
            PageActivated?.Invoke(indices[0]);
        }
    }

    public void SetDeviceScale(double scale)
    {
        scale = scale <= 0 ? 1.0 : scale;
        if (System.Math.Abs(scale - _deviceScale) > 0.001)
        {
            _deviceScale = scale;
        }
    }

    /// <summary>Mark which page the pane is on (the highlighted thumbnail).</summary>
    public void SetCurrentPage(int oneBasedPage)
    {
        foreach (ThumbnailViewModel thumb in Thumbnails)
        {
            thumb.IsCurrent = thumb.PageNumber == oneBasedPage;
        }

        if (oneBasedPage >= 1 && oneBasedPage <= Thumbnails.Count)
        {
            CurrentPageChanged?.Invoke(oneBasedPage - 1);
        }
    }

    /// <summary>The view reports which thumbnail indices are visible; render those.</summary>
    public void RequestRange(int first, int last)
    {
        first = System.Math.Max(0, first);
        last = System.Math.Min(Thumbnails.Count - 1, last);

        for (int i = first; i <= last; i++)
        {
            ThumbnailViewModel thumb = Thumbnails[i];
            int pixelWidth = (int)System.Math.Round(ThumbnailWidth * _deviceScale);
            int pixelHeight = (int)System.Math.Round(ThumbnailWidth * thumb.Aspect * _deviceScale);
            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                continue;
            }

            if (thumb.Image is not null && thumb.RenderedPixelWidth == pixelWidth)
            {
                continue;
            }

            _queue.Enqueue(new RenderRequest
            {
                Owner = this,
                Document = _document,
                PageIndex = thumb.PageIndex,
                PixelWidth = pixelWidth,
                PixelHeight = pixelHeight,
                OnRendered = OnThumbnailRendered,
            });
        }
    }

    private void Populate(IReadOnlyList<PdfSize> pageSizes)
    {
        for (int i = 0; i < pageSizes.Count; i++)
        {
            PdfSize size = pageSizes[i];
            Thumbnails.Add(new ThumbnailViewModel(i, size.Width > 0 ? size.Height / size.Width : 1.294));
        }
    }

    private void OnThumbnailRendered(int pageIndex, int pixelWidth, ImageSource image)
    {
        if (pageIndex < 0 || pageIndex >= Thumbnails.Count)
        {
            return;
        }

        ThumbnailViewModel thumb = Thumbnails[pageIndex];
        int expected = (int)System.Math.Round(ThumbnailWidth * _deviceScale);
        if (pixelWidth != expected)
        {
            return;
        }

        thumb.RenderedPixelWidth = pixelWidth;
        thumb.Image = image;
    }
}
