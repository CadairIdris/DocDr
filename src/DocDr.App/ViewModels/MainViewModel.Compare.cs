using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DocDr.App.Views;
using DocDr.Pdf;
using Microsoft.Win32;

namespace DocDr.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly List<VisualCompareWindow> _compareWindows = [];

    /// <summary>
    /// Tools ▾ "Compare with…": the current tab is the old version, the picked file the new one
    /// (Swap in the window reverses them). A file that's already open in a tab is compared in its
    /// current state rather than loaded twice.
    /// </summary>
    [RelayCommand]
    private async Task CompareWithAsync()
    {
        if (SelectedTab is not { } tab)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Compare with — pick the other version",
            Filter = "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = tab.FilePath is { } current ? Path.GetDirectoryName(current) : null,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string path = dialog.FileName;
        PdfDocument newDoc;
        bool ownsNew;
        if (Tabs.FirstOrDefault(t => t.FilePath is { } p && PathsEqual(p, path)) is { } open)
        {
            newDoc = open.Document;
            ownsNew = false;
        }
        else
        {
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                newDoc = await Task.Run(() => PdfDocument.Load(path)).ConfigureAwait(true);
                ownsNew = true;
            }
            catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show($"Couldn't open {Path.GetFileName(path)}:\n{ex.Message}", "Compare",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        string oldName = tab.FilePath is { } oldPath ? Path.GetFileName(oldPath) : tab.Title.TrimEnd(' ', '•');
        var viewModel = new VisualCompareViewModel(tab.Document, oldName, ownsOld: false, newDoc, Path.GetFileName(path), ownsNew);
        var window = new VisualCompareWindow(viewModel) { Owner = Application.Current?.MainWindow };
        window.Closed += (_, _) =>
        {
            _compareWindows.Remove(window);
            viewModel.Dispose();
        };
        _compareWindows.Add(window);
        window.Show();
        viewModel.Start();
    }

    /// <summary>A compare window borrowing a closing tab's document closes with it.</summary>
    private void CloseComparisonsUsing(PdfDocument document)
    {
        foreach (VisualCompareWindow window in _compareWindows.ToList())
        {
            if (window.ViewModel.Uses(document))
            {
                window.Close();
            }
        }
    }
}
