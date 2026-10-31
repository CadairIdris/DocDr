using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

public enum MeasureScaleOutcome
{
    Cancel,

    /// <summary>Use <see cref="MeasureScaleViewModel.Result"/>.</summary>
    Apply,

    /// <summary>Close and let the user draw a line of known length on the page.</summary>
    Calibrate,
}

/// <summary>
/// Backs the "Set scale" dialog: either a drawing ratio (1 : 50, reported in m) or — after a line
/// has been drawn on the page — the real length of that line (calibration). Either way the user
/// picks the precision and whether it applies to this page or the whole document.
/// </summary>
public sealed partial class MeasureScaleViewModel : ObservableObject
{
    private readonly double? _calibrationPoints;

    /// <param name="current">The scale in force now (pre-fills the fields).</param>
    /// <param name="calibrationPoints">Length (PDF points) of a just-drawn calibration line, or null
    /// for the ratio form.</param>
    /// <param name="pageLabel">The page the dialog was opened for ("12"), for the scope option.</param>
    public MeasureScaleViewModel(PdfMeasureScale current, double? calibrationPoints, string pageLabel)
    {
        _calibrationPoints = calibrationPoints;
        PageLabel = pageLabel;
        _unit = PdfMeasureScale.Units.Contains(current.Unit) ? current.Unit : "m";
        _decimals = Math.Clamp(current.Decimals, 0, 4);

        // Pre-fill the ratio when the current scale is a plain 1:N.
        _ratioText = current.Label.StartsWith("1:", StringComparison.Ordinal) ? current.Label[2..] : "50";

        if (calibrationPoints is { } pts)
        {
            // What the drawn line measures at the current scale — usually a decent starting guess.
            double guess = pts * current.UnitsPerPoint;
            _knownLengthText = guess.ToString("0.###", CultureInfo.CurrentCulture);
        }
    }

    public bool IsCalibration => _calibrationPoints is not null;

    public bool IsRatio => !IsCalibration;

    public string Title => IsCalibration ? "Calibrate scale" : "Set scale";

    public string PageLabel { get; }

    public string ThisPageText => $"This page ({PageLabel})";

    public IReadOnlyList<string> Units => PdfMeasureScale.Units;

    public IReadOnlyList<int> DecimalChoices { get; } = [0, 1, 2, 3, 4];

    [ObservableProperty]
    private string _ratioText = "50";

    [ObservableProperty]
    private string _knownLengthText = string.Empty;

    [ObservableProperty]
    private string _unit;

    [ObservableProperty]
    private int _decimals;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyToThisPage))]
    private bool _applyToAllPages = true;

    /// <summary>The other radio button of the scope pair.</summary>
    public bool ApplyToThisPage
    {
        get => !ApplyToAllPages;
        set => ApplyToAllPages = !value;
    }

    [ObservableProperty]
    private string _error = string.Empty;

    public MeasureScaleOutcome Outcome { get; private set; }

    public PdfMeasureScale? Result { get; private set; }

    /// <summary>Set by the window; called once the dialog should close.</summary>
    public Action? Close { get; set; }

    [RelayCommand]
    private void Apply()
    {
        try
        {
            if (IsCalibration)
            {
                if (!TryParse(KnownLengthText, out double known))
                {
                    Error = "Enter the real length of the line you drew.";
                    return;
                }

                Result = PdfMeasureScale.Calibrate(_calibrationPoints!.Value, known, Unit, Decimals);
            }
            else
            {
                string ratio = RatioText.Trim();
                if (ratio.StartsWith("1:", StringComparison.Ordinal))
                {
                    ratio = ratio[2..];
                }

                if (!TryParse(ratio, out double n))
                {
                    Error = "Enter the scale as a number, e.g. 50 for 1:50.";
                    return;
                }

                Result = PdfMeasureScale.FromDrawingRatio(n, Unit, Decimals);
            }
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return;
        }

        Outcome = MeasureScaleOutcome.Apply;
        Close?.Invoke();
    }

    [RelayCommand]
    private void Calibrate()
    {
        Outcome = MeasureScaleOutcome.Calibrate;
        Close?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        Outcome = MeasureScaleOutcome.Cancel;
        Close?.Invoke();
    }

    private static bool TryParse(string text, out double value) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        && value > 0 && double.IsFinite(value);
}
