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

    /// <summary>A crisp native-resolution render of just the on-screen slice of this page, laid over
    /// the (size-capped, upscaled) <see cref="Image"/> when the zoom is past the render cap. Null
    /// otherwise.</summary>
    [ObservableProperty]
    private ImageSource? _detailImage;

    /// <summary>Placement of <see cref="DetailImage"/> within the page, in page-local DIP.</summary>
    [ObservableProperty]
    private double _detailLeft;

    [ObservableProperty]
    private double _detailTop;

    [ObservableProperty]
    private double _detailWidth;

    [ObservableProperty]
    private double _detailHeight;

    /// <summary>What the current (or in-flight) <see cref="DetailImage"/> was requested for —
    /// (offsetX, offsetY, tileW, tileH, fullW, fullH) in device px. Skips a re-request when the
    /// viewport hasn't really moved.</summary>
    internal (int, int, int, int, int, int) DetailRequestKey { get; set; }

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

    /// <summary>A frozen brush (opacity baked in) — bound to <c>Polyline.Stroke</c> directly, since a
    /// <c>{Binding}</c> on an inline <c>SolidColorBrush.Color</c> has no reliable data context.</summary>
    [ObservableProperty]
    private System.Windows.Media.Brush _inkPreviewBrush = System.Windows.Media.Brushes.Transparent;

    [ObservableProperty]
    private double _inkPreviewThickness = 12;

    /// <summary>The shape (text box / cloud) being dragged out on this page right now (DIP space), or null.</summary>
    [ObservableProperty]
    private System.Windows.Rect? _shapePreview;

    /// <summary>The callout leader line during a drag (tip → box), in DIP space; null otherwise.</summary>
    [ObservableProperty]
    private System.Windows.Media.PointCollection? _shapePreviewLeader;

    /// <summary>The callout arrowhead during a drag, in DIP space; null otherwise.</summary>
    [ObservableProperty]
    private System.Windows.Media.PointCollection? _shapePreviewArrow;

    /// <summary>True while a container for this slot is realised in the visual tree.</summary>
    public bool IsRealized { get; set; }
}
