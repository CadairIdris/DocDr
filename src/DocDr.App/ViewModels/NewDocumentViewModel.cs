using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>Backs the "New document" dialog: page size + orientation for a blank page.</summary>
public sealed partial class NewDocumentViewModel : ObservableObject
{
    private const string Custom = "Custom";
    private readonly AppSettings _settings;

    public NewDocumentViewModel(AppSettings settings)
    {
        _settings = settings;
        _selectedSize = settings.LastPageSize is { Length: > 0 } s && (s == Custom || PageSizes.StandardMm(s) is not null)
            ? s
            : "A4";
        _landscape = settings.LastPageLandscape;
        (_widthMm, _heightMm) = _selectedSize == Custom
            ? (settings.LastCustomWidthMm, settings.LastCustomHeightMm)
            : PageSizes.StandardMm(_selectedSize) ?? (210d, 297d);
    }

    public IReadOnlyList<string> SizeNames { get; } = [.. GetNames()];

    private static IEnumerable<string> GetNames()
    {
        foreach ((string name, _, _) in PageSizes.Standard)
        {
            yield return name;
        }

        yield return Custom;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom))]
    private string _selectedSize;

    [ObservableProperty]
    private double _widthMm;

    [ObservableProperty]
    private double _heightMm;

    [ObservableProperty]
    private bool _landscape;

    public bool IsCustom => SelectedSize == Custom;

    /// <summary>Set by the host; invoked with the chosen size (points), or null on cancel.</summary>
    public Action<PdfSize?>? Confirmed { get; set; }

    partial void OnSelectedSizeChanged(string value)
    {
        if (PageSizes.StandardMm(value) is { } mm)
        {
            (WidthMm, HeightMm) = mm;
        }
    }

    [RelayCommand]
    private void Create()
    {
        double w = Math.Clamp(WidthMm, 10, 2000);
        double h = Math.Clamp(HeightMm, 10, 2000);
        // Standard sizes hold portrait mm; FromMm swaps for landscape. Custom is taken literally
        // (FromMm only swaps when it isn't already the wider way round).
        PdfSize size = PageSizes.FromMm(w, h, Landscape);

        _settings.LastPageSize = SelectedSize;
        _settings.LastPageLandscape = Landscape;
        if (IsCustom)
        {
            _settings.LastCustomWidthMm = w;
            _settings.LastCustomHeightMm = h;
        }

        _settings.Save();
        Confirmed?.Invoke(size);
    }

    [RelayCommand]
    private void Cancel() => Confirmed?.Invoke(null);
}
