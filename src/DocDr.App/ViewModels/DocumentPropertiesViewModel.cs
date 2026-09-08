using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocDr.Pdf;

namespace DocDr.App.ViewModels;

/// <summary>Backs the modal Document Properties dialog: editable Info-dictionary fields plus
/// read-only facts about the file.</summary>
public sealed partial class DocumentPropertiesViewModel : ObservableObject
{
    private readonly PdfDocument _document;

    public DocumentPropertiesViewModel(PdfDocument document)
    {
        _document = document;
        PdfDocumentInfo info = document.Info;

        _title = info.Title;
        _author = info.Author;
        _subject = info.Subject;
        _keywords = info.Keywords;
        _creator = info.Creator;

        Producer = OrDash(info.Producer);
        Created = OrDash(PdfDate.ForDisplay(info.CreationDate));
        Modified = OrDash(PdfDate.ForDisplay(info.ModificationDate));
        PageCountText = document.PageCount.ToString();
        Features = BuildFeatureLine(document);
        FilePath = document.FilePath ?? "(unsaved)";
        FileSizeText = document.FilePath is { } p && File.Exists(p)
            ? $"{new FileInfo(p).Length / 1024.0:N0} KB"
            : "—";
    }

    // Editable
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _author;
    [ObservableProperty] private string _subject;
    [ObservableProperty] private string _keywords;
    [ObservableProperty] private string _creator;

    // Read-only
    public string Producer { get; }
    public string Created { get; }
    public string Modified { get; }
    public string PageCountText { get; }
    public string FilePath { get; }
    public string FileSizeText { get; }

    /// <summary>"Tagged · Encrypted · Page labels" — the structural traits worth surfacing.</summary>
    public string Features { get; }

    private static string BuildFeatureLine(PdfDocument document)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (document.IsTagged)
        {
            parts.Add("Tagged (accessible)");
        }

        if (document.IsEncrypted)
        {
            parts.Add("Encrypted");
        }

        if (document.HasPageLabels)
        {
            parts.Add("Custom page labels");
        }

        return parts.Count > 0 ? string.Join("  ·  ", parts) : "—";
    }

    /// <summary>Set by the window so the commands can close it.</summary>
    public Action<bool>? CloseRequested { get; set; }

    [RelayCommand]
    private void Ok()
    {
        _document.UpdateInfo(_document.Info with
        {
            Title = Title.Trim(),
            Author = Author.Trim(),
            Subject = Subject.Trim(),
            Keywords = Keywords.Trim(),
            Creator = Creator.Trim(),
        });

        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    private static string OrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
