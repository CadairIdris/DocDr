using System.Collections.Generic;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>
/// One page's placeholder in a pane: its on-screen size at the current zoom, its (lazily
/// rendered) bitmap, and any search highlights that fall on it.
/// </summary>
public sealed partial class PageSlotViewModel : ObservableObject
{
    public PageSlotViewModel(int pageIndex, PdfSize sizePoints)
    {
        PageIndex = pageIndex;
        SizePoints = sizePoints;
    }

    /// <summary>Zero-based page index.</summary>
    public int PageIndex { get; }

    /// <summary>One-based page number for display.</summary>
    public int PageNumber => PageIndex + 1;

    public PdfSize SizePoints { get; }

    /// <summary>Layout width in DIP at the current zoom.</summary>
    [ObservableProperty]
    private double _layoutWidth;

    /// <summary>Layout height in DIP at the current zoom.</summary>
    [ObservableProperty]
    private double _layoutHeight;

    /// <summary>The rendered page image, or null until it has been produced.</summary>
    [ObservableProperty]
    private ImageSource? _image;

    /// <summary>Pixel width the current <see cref="Image"/> was rendered at (0 if none).</summary>
    public int RenderedPixelWidth { get; set; }

    [ObservableProperty]
    private IReadOnlyList<HighlightRect> _highlights = [];

    /// <summary>Annotation overlays (highlights + note markers) on this page, in DIP space.</summary>
    [ObservableProperty]
    private IReadOnlyList<AnnotationVisual> _annotations = [];

    /// <summary>Clickable link regions (TOC entries, cross-references, URLs) on this page, in DIP space.</summary>
    [ObservableProperty]
    private IReadOnlyList<LinkVisual> _links = [];

    /// <summary>Live text-selection rectangles during a drag, in DIP space.</summary>
    [ObservableProperty]
    private IReadOnlyList<System.Windows.Rect> _selectionRects = [];

    /// <summary>The highlighter stroke being drawn on this page right now (DIP space), or null.</summary>
    [ObservableProperty]
    private System.Windows.Media.PointCollection? _inkPreview;

    [ObservableProperty]
    private Color _inkPreviewBrush = Colors.Yellow;

    [ObservableProperty]
    private double _inkPreviewThickness = 12;

    /// <summary>The shape (text box / cloud) being dragged out on this page right now (DIP space), or null.</summary>
    [ObservableProperty]
    private System.Windows.Rect? _shapePreview;

    /// <summary>True while a container for this slot is realised in the visual tree.</summary>
    public bool IsRealized { get; set; }
}
