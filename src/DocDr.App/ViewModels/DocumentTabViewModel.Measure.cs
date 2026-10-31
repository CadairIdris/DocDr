using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Views;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>
/// Measuring: the drawing scale(s) for this document, the Set scale / Calibrate dialogs, and CSV
/// export. A scale is a session setting — every measurement stores the scale it was taken at, so
/// on open the scales are inferred back from the measurements already in the file.
/// </summary>
public sealed partial class DocumentTabViewModel
{
    /// <summary>The scale for pages without their own; null until set or inferred (→ the 1:1 default).</summary>
    private PdfMeasureScale? _documentScale;

    /// <summary>Per-page overrides ("This page" in the scale dialog), by page index.</summary>
    private readonly Dictionary<int, PdfMeasureScale> _pageScales = [];

    /// <summary>The scale new measurements on <paramref name="pageIndex"/> use.</summary>
    public PdfMeasureScale ScaleFor(int pageIndex) =>
        _pageScales.TryGetValue(pageIndex, out PdfMeasureScale? s) ? s : _documentScale ?? PdfMeasureScale.Default;

    /// <summary>Whether a measuring tool (or calibration) is armed — lights the toolbar's Measure button.</summary>
    public bool IsMeasureToolActive => ShapeTool is ShapeTool.MeasureDistance or ShapeTool.MeasurePerimeter
        or ShapeTool.MeasureArea or ShapeTool.Calibrate;

    private void InitMeasuring()
    {
        InferScales();
        foreach (PdfPaneViewModel pane in new[] { LeftPane, RightPane })
        {
            pane.ScaleProvider = ScaleFor;
            pane.SetScaleRequested += OnSetScaleRequested;
            pane.CalibrationMeasured += OnCalibrationMeasured;
        }
    }

    private void DisposeMeasuring()
    {
        foreach (PdfPaneViewModel pane in new[] { LeftPane, RightPane })
        {
            pane.ScaleProvider = null;
            pane.SetScaleRequested -= OnSetScaleRequested;
            pane.CalibrationMeasured -= OnCalibrationMeasured;
        }
    }

    /// <summary>Recover the scales from the file's measurements: the newest measurement's scale
    /// becomes the document scale; a page whose newest measurement used a different one keeps it.</summary>
    private void InferScales()
    {
        _pageScales.Clear();
        _documentScale = null;

        var latestPerPage = new Dictionary<int, PdfAnnotation>();
        PdfAnnotation? latest = null;
        for (int page = 0; page < Document.PageCount; page++)
        {
            foreach (PdfAnnotation a in Document.GetAnnotations(page))
            {
                if (!a.IsMeasurement || a.Scale is null)
                {
                    continue;
                }

                if (!latestPerPage.TryGetValue(page, out PdfAnnotation? have) || Newer(a, have))
                {
                    latestPerPage[page] = a;
                }

                if (latest is null || Newer(a, latest))
                {
                    latest = a;
                }
            }
        }

        _documentScale = latest?.Scale;
        foreach ((int page, PdfAnnotation a) in latestPerPage)
        {
            if (a.Scale is { } s && s != _documentScale)
            {
                _pageScales[page] = s;
            }
        }

        static bool Newer(PdfAnnotation x, PdfAnnotation y) =>
            (x.Created ?? DateTimeOffset.MinValue) > (y.Created ?? DateTimeOffset.MinValue);
    }

    /// <summary>A structural edit (delete / insert / reorder) shifts page indices, so per-page
    /// overrides no longer point at the right pages: drop them, keep the document scale.</summary>
    private void OnPagesChangedForMeasuring()
    {
        _pageScales.Clear();
        RefreshPaneScales();
    }

    private void RefreshPaneScales()
    {
        LeftPane.RefreshScale();
        RightPane.RefreshScale();
    }

    [RelayCommand]
    private void SetScale() => OpenScaleDialog(Math.Max(0, LeftPane.CurrentPage - 1), calibrationPoints: null);

    /// <summary>Arm calibration: click two ends of a known dimension, then enter its length.</summary>
    [RelayCommand]
    private void CalibrateScale() => ShapeTool = ShapeTool.Calibrate;

    [RelayCommand]
    private void ArmMeasureTool(ShapeTool tool) => ShapeTool = ShapeTool == tool ? ShapeTool.None : tool;

    private void OnSetScaleRequested() => SetScale();

    private void OnCalibrationMeasured(int pageIndex, double pagePoints) =>
        // Raised from a mouse-down handler: a modal opened synchronously there doesn't activate.
        Application.Current?.Dispatcher.BeginInvoke(
            () => OpenScaleDialog(pageIndex, pagePoints),
            System.Windows.Threading.DispatcherPriority.Input);

    private void OpenScaleDialog(int pageIndex, double? calibrationPoints)
    {
        var vm = new MeasureScaleViewModel(ScaleFor(pageIndex), calibrationPoints, PageLabelFor(pageIndex))
        {
            // Opened for a page that has its own scale → default the scope to that page.
            ApplyToAllPages = !_pageScales.ContainsKey(pageIndex),
        };
        var window = new MeasureScaleWindow(vm) { Owner = Application.Current?.MainWindow };
        window.ShowDialog();

        switch (vm.Outcome)
        {
            case MeasureScaleOutcome.Calibrate:
                CalibrateScale();
                break;
            case MeasureScaleOutcome.Apply when vm.Result is { } scale:
                if (vm.ApplyToAllPages)
                {
                    _documentScale = scale;
                    _pageScales.Clear();
                }
                else
                {
                    _pageScales[pageIndex] = scale;
                }

                RefreshPaneScales();
                break;
        }
    }

    [RelayCommand]
    private void ExportMeasurements()
    {
        var rows = new List<(int Page, PdfAnnotation A)>();
        for (int page = 0; page < Document.PageCount; page++)
        {
            rows.AddRange(Document.GetAnnotations(page).Where(a => a.IsMeasurement).Select(a => (page, a)));
        }

        if (rows.Count == 0)
        {
            MessageBox.Show("This document has no measurements yet.", "Export measurements",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export measurements",
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"{Path.GetFileNameWithoutExtension(_baseTitle)} measurements.csv",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, MeasurementsCsv(rows, PageLabelFor), new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not write the file: {ex.Message}", "Export measurements",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>One row per measurement. Values are plain invariant numbers (spreadsheet-friendly);
    /// text cells that a spreadsheet would run as a formula are neutralised.</summary>
    internal static string MeasurementsCsv(IReadOnlyList<(int Page, PdfAnnotation A)> rows, Func<int, string> pageLabel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Page,Type,Value,Unit,Scale,Points,Author,Created");
        foreach ((int page, PdfAnnotation a) in rows)
        {
            PdfMeasureScale scale = a.Scale ?? PdfMeasureScale.Default;
            double value = PdfMeasure.Value(a.Kind, a.Vertices, scale);
            string unit = a.Kind == PdfAnnotationKind.MeasureArea ? scale.Unit + "²" : scale.Unit;
            sb.AppendJoin(',',
                Cell(pageLabel(page)),
                Cell(AnnotationKinds.Label(a.Kind)),
                Math.Round(value, Math.Clamp(scale.Decimals, 0, 6)).ToString(CultureInfo.InvariantCulture),
                Cell(unit),
                Cell(scale.Label),
                a.Vertices.Count.ToString(CultureInfo.InvariantCulture),
                Cell(a.Author ?? string.Empty),
                Cell(a.Created?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty));
            sb.AppendLine();
        }

        return sb.ToString();

        static string Cell(string text)
        {
            if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            {
                text = "'" + text;
            }

            return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }
    }
}
