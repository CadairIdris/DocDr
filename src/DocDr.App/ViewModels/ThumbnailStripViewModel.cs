using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DocDr.App.Services;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>
/// The collapsible page-thumbnail navigation strip for a document tab. Shares the document,
/// render queue and page cache with the panes; renders only the thumbnails the view reports as
/// on-screen.
/// </summary>
public sealed partial class ThumbnailStripViewModel : ObservableObject
{
    /// <summary>Thumbnail width in DIP. Kept small and fixed.</summary>
    public const double ThumbnailWidth = 118.0;

    private readonly PdfDocument _document;
    private readonly BackgroundRenderQueue _queue;
    private double _deviceScale = 1.0;
    private bool _suppressActivation;

    public ThumbnailStripViewModel(PdfDocument document, IReadOnlyList<PdfSize> pageSizes, BackgroundRenderQueue queue)
    {
        _document = document;
        _queue = queue;

        Thumbnails = new ObservableCollection<ThumbnailViewModel>(
            pageSizes.Select((size, index) =>
                new ThumbnailViewModel(index, size.Width > 0 ? size.Height / size.Width : 1.294)));
    }

    public ObservableCollection<ThumbnailViewModel> Thumbnails { get; }

    /// <summary>Raised when the user picks a thumbnail; carries the 0-based page index.</summary>
    public event Action<int>? PageActivated;

    [ObservableProperty]
    private ThumbnailViewModel? _selected;

    partial void OnSelectedChanged(ThumbnailViewModel? value)
    {
        if (value is not null && !_suppressActivation)
        {
            PageActivated?.Invoke(value.PageIndex);
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

    /// <summary>Mark which page the pane is on (highlights that thumbnail, moves the selection).</summary>
    public void SetCurrentPage(int oneBasedPage)
    {
        foreach (ThumbnailViewModel thumb in Thumbnails)
        {
            thumb.IsCurrent = thumb.PageNumber == oneBasedPage;
        }

        if (oneBasedPage >= 1 && oneBasedPage <= Thumbnails.Count)
        {
            _suppressActivation = true;
            Selected = Thumbnails[oneBasedPage - 1];
            _suppressActivation = false;
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
