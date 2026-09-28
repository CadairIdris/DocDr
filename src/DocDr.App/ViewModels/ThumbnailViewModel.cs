using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>One page's thumbnail in the navigation strip.</summary>
public sealed partial class ThumbnailViewModel : ObservableObject
{
    public ThumbnailViewModel(int pageIndex, PdfSize sizePoints, double aspect)
    {
        PageIndex = pageIndex;
        SizePoints = sizePoints;
        Aspect = aspect;
    }

    public int PageIndex { get; }

    /// <summary>The page's (rotated, on-screen) size in points.</summary>
    public PdfSize SizePoints { get; }

    public int PageNumber => PageIndex + 1;

    /// <summary>Height / width of the page, for sizing the thumbnail box.</summary>
    public double Aspect { get; }

    [ObservableProperty]
    private ImageSource? _image;

    /// <summary>True when this is the page the pane is currently showing.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    internal int RenderedPixelWidth { get; set; }
}
