using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Services;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>One detected watermark in the review dialog.</summary>
public sealed partial class WatermarkRowViewModel : ObservableObject
{
    public WatermarkRowViewModel(WatermarkCandidate candidate)
    {
        Candidate = candidate;
        _selected = candidate.OnEveryPage;
    }

    public WatermarkCandidate Candidate { get; }

    [ObservableProperty]
    private bool _selected;

    public string Label => Candidate.Label;

    public string Kind => Candidate.Kind == WatermarkKind.Image ? "Image" : "Text";

    public string Detail => Candidate.OnEveryPage
        ? $"on every page ({Candidate.TotalPages})"
        : $"on {Candidate.PageCount} of {Candidate.TotalPages} pages";

    public string? Warning => Candidate.SafeToRemove
        ? null
        : "Some pages have nothing else on them — removing this would leave them blank.";

    public bool HasWarning => Warning is not null;
}

/// <summary>Backs the "Remove watermark" review dialog: a before/after preview and a checklist.</summary>
public sealed partial class WatermarkReviewViewModel : ObservableObject
{
    private readonly PdfDocument _document;

    public WatermarkReviewViewModel(PdfDocument document, IReadOnlyList<WatermarkCandidate> candidates)
    {
        _document = document;
        Rows = candidates.Select(c => new WatermarkRowViewModel(c)).ToArray();
        foreach (WatermarkRowViewModel row in Rows)
        {
            row.PropertyChanged += OnRowChanged;
        }
    }

    /// <summary>Kick off the before/after preview render. Call once the dialog is shown.</summary>
    public void StartPreview() => _ = LoadPreviewsAsync();

    public IReadOnlyList<WatermarkRowViewModel> Rows { get; }

    [ObservableProperty]
    private ImageSource? _beforePreview;

    [ObservableProperty]
    private ImageSource? _afterPreview;

    [ObservableProperty]
    private bool _isLoadingPreview = true;

    public bool AnySelected => Rows.Any(r => r.Selected);

    /// <summary>Set by the host; called with the chosen candidates, or null on cancel.</summary>
    public Action<IReadOnlyList<WatermarkCandidate>?>? Closed { get; set; }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WatermarkRowViewModel.Selected))
        {
            OnPropertyChanged(nameof(AnySelected));
            RemoveCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(AnySelected))]
    private void Remove() =>
        Closed?.Invoke(Rows.Where(r => r.Selected).Select(r => r.Candidate).ToArray());

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(null);

    private async Task LoadPreviewsAsync()
    {
        try
        {
            (ImageSource before, ImageSource after) = await Task.Run(() =>
            {
                var service = new PageImageService(new PageRenderer());
                const int width = 460;
                PdfSize size = _document.GetPageSize(0);
                int height = size.Width > 0 ? (int)Math.Round(width * size.Height / size.Width) : width;

                ImageSource b = service.Render(_document, 0, width, height, default);

                using PdfDocument copy = PdfDocument.Load(_document.SaveToBytes());
                copy.RemoveWatermarks(Rows.Select(r => r.Candidate).ToArray());
                ImageSource a = service.Render(copy, 0, width, height, default);
                return (b, a);
            }).ConfigureAwait(true);

            BeforePreview = before;
            AfterPreview = after;
        }
        catch (Exception ex) when (ex is PdfException or InvalidOperationException)
        {
            // no preview — the checklist still works
        }
        finally
        {
            IsLoadingPreview = false;
        }
    }
}
