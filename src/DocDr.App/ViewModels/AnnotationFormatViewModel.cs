using System;
using System.Collections.Generic;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>
/// Backs the floating format toolbar shown while a shape / line / text-box annotation is selected.
/// A fresh instance is built from the selected <see cref="PdfAnnotation"/> whenever the selection or
/// the annotation changes; the two-way properties and the commands apply an edit through the owning
/// <see cref="PdfPaneViewModel"/>.
/// </summary>
public sealed partial class AnnotationFormatViewModel : ObservableObject
{
    private readonly IAnnotationFormatHost _owner;
    private readonly Guid _id;
    private readonly bool _loading;

    public AnnotationFormatViewModel(IAnnotationFormatHost owner, PdfAnnotation a)
    {
        _loading = true;
        _owner = owner;
        _id = a.Id;
        Kind = a.Kind;

        _strokeColorArgb = a.ColorArgb == 0 ? 0xFF000000 : a.ColorArgb;
        _lineWidth = NearestWidth(a.EffectiveLineWidth);
        _dashed = a.Dashed;
        _fillArgb = a.FillArgb;
        _hasBorder = !a.Borderless;
        _fontSize = a.FontSize > 0 ? a.FontSize : PdfAnnotation.DefaultFontSize;
        _matchTextToOutline = a.TextColorArgb is { } tc && (tc & 0xFFFFFF) == (a.ColorArgb & 0xFFFFFF);
        _loading = false;
    }

    public PdfAnnotationKind Kind { get; }

    public string KindLabel => AnnotationKinds.Label(Kind);

    // --- which control groups this kind shows -------------------------------------------

    private bool IsBoxShape => Kind is PdfAnnotationKind.Rectangle or PdfAnnotationKind.Ellipse;

    private bool IsLine => Kind is PdfAnnotationKind.Line or PdfAnnotationKind.Arrow;

    private bool IsText => Kind is PdfAnnotationKind.TextBox or PdfAnnotationKind.Callout;

    public bool ShowStrokeColor => true;

    public bool ShowLineWidth => IsBoxShape || IsLine || Kind == PdfAnnotationKind.Cloud || IsText;

    public bool ShowDashed => IsBoxShape || IsLine;

    public bool ShowFill => IsBoxShape;

    public bool ShowTextColor => IsText;

    public bool ShowBorderToggle => IsText;

    public bool ShowFontSize => IsText;

    // --- swatches ----------------------------------------------------------------------

    public IReadOnlyList<string> Swatches { get; } = AnnotationColors.Keys;

    public IReadOnlyList<double> LineWidths { get; } = [0.5, 1, 1.5, 2, 3, 4];

    // --- current values --------------------------------------------------------------

    [ObservableProperty]
    private uint _strokeColorArgb;

    [ObservableProperty]
    private double _lineWidth;

    [ObservableProperty]
    private bool _dashed;

    [ObservableProperty]
    private uint? _fillArgb;

    [ObservableProperty]
    private bool _matchTextToOutline;

    [ObservableProperty]
    private bool _hasBorder;

    [ObservableProperty]
    private double _fontSize;

    public bool HasFill => FillArgb is not null;

    public string FontSizeLabel => $"{FontSize:0.#} pt";

    partial void OnLineWidthChanged(double value)
    {
        if (!_loading)
        {
            Apply(a => a with { LineWidth = value });
        }
    }

    partial void OnDashedChanged(bool value)
    {
        if (!_loading)
        {
            Apply(a => a with { Dashed = value });
        }
    }

    partial void OnHasBorderChanged(bool value)
    {
        if (!_loading)
        {
            Apply(a => a with { Borderless = !value });
        }
    }

    partial void OnMatchTextToOutlineChanged(bool value)
    {
        if (!_loading)
        {
            Apply(a => a with { TextColorArgb = value ? StrokeColorArgb : null });
        }
    }

    partial void OnFillArgbChanged(uint? value) => OnPropertyChanged(nameof(HasFill));

    // --- commands -------------------------------------------------------------------

    /// <summary>Pick an outline colour: a preset key, or "Custom" to open the picker.</summary>
    [RelayCommand]
    private void PickStroke(string key)
    {
        if (Resolve(key, StrokeColorArgb) is { } value)
        {
            StrokeColorArgb = value;
            Apply(a => a with
            {
                ColorArgb = value,
                TextColorArgb = MatchTextToOutline ? value : a.TextColorArgb,
            });
        }
    }

    [RelayCommand]
    private void PickFill(string key)
    {
        if (Resolve(key, FillArgb ?? StrokeColorArgb) is { } value)
        {
            uint tint = (value & 0x00FFFFFF) | 0x40000000; // a translucent wash reads better over content
            FillArgb = tint;
            Apply(a => a with { FillArgb = tint });
        }
    }

    [RelayCommand]
    private void ClearFill()
    {
        FillArgb = null;
        Apply(a => a with { FillArgb = null });
    }

    [RelayCommand]
    private void StepFontSize(string delta)
    {
        FontSize = Math.Clamp(FontSize + (delta == "-" ? -1 : 1), 6, 96);
        OnPropertyChanged(nameof(FontSizeLabel));
        _owner.ResizeTextBoxForFont(_id, FontSize);
    }

    [RelayCommand]
    private void Delete() => _owner.DeleteAnnotation(_id);

    private uint? Resolve(string key, uint fallback) => key == AnnotationColors.Custom
        ? _owner.PickCustomColor(fallback)
        : AnnotationColors.ToArgb(key);

    private void Apply(Func<PdfAnnotation, PdfAnnotation> mutate) => _owner.ApplyFormat(_id, mutate);

    private double NearestWidth(double width)
    {
        double best = LineWidths[0];
        foreach (double w in LineWidths)
        {
            if (Math.Abs(w - width) < Math.Abs(best - width))
            {
                best = w;
            }
        }

        return best;
    }
}
