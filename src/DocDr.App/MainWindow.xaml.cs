using System;
using System.IO;
using System.Linq;
using System.Windows;
using DocDr.App.ViewModels;

namespace DocDr.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        bool acceptable = TryGetPdfPaths(e.Data, out _);
        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = acceptable ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        Point p = e.GetPosition(this);
        if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (TryGetPdfPaths(e.Data, out string[] paths) && DataContext is MainViewModel viewModel)
        {
            e.Handled = true;
            foreach (string path in paths)
            {
                _ = viewModel.OpenPathAsync(path);
            }
        }
    }

    private static bool TryGetPdfPaths(IDataObject data, out string[] paths)
    {
        paths = [];
        if (!data.GetDataPresent(DataFormats.FileDrop) ||
            data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return false;
        }

        paths = files
            .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(f))
            .ToArray();
        return paths.Length > 0;
    }
}
