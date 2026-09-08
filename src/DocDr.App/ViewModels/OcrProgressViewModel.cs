using System;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>Backs the modal OCR progress dialog: a bar, a status line and a Cancel button.</summary>
public sealed partial class OcrProgressViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cts = new();
    private readonly int _totalPages;
    private int _done;

    public OcrProgressViewModel(int totalPages)
    {
        _totalPages = Math.Max(1, totalPages);
        _status = $"Recognising {totalPages} page{(totalPages == 1 ? "" : "s")}…";
    }

    public CancellationToken Token => _cts.Token;

    /// <summary>Raised when the run finishes (or is cancelled) and the dialog should close.</summary>
    public event Action? Closed;

    [ObservableProperty]
    private string _status;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _isIndeterminate = true;

    [ObservableProperty]
    private bool _canCancel = true;

    /// <summary>Fed each <see cref="OcrProgress"/> on the UI thread (via a <see cref="Progress{T}"/>).</summary>
    public void Report(OcrProgress p)
    {
        _done++;
        IsIndeterminate = false;
        Progress = 100.0 * _done / _totalPages;
        if (CanCancel)
        {
            Status = $"Recognised page {p.Page} — {_done} of {_totalPages}";
        }
    }

    /// <summary>Close the dialog — called by the host when the work completes.</summary>
    public void Finish() => Closed?.Invoke();

    [RelayCommand]
    private void Cancel()
    {
        if (!CanCancel)
        {
            return;
        }

        CanCancel = false;
        IsIndeterminate = true;
        Status = "Finishing the current page…";
        _cts.Cancel();
    }
}
