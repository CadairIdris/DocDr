using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using DocDr.App.ViewModels;

namespace DocDr.App;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private WindowState _stateBeforeReading = WindowState.Normal;
    private WindowStyle _styleBeforeReading = WindowStyle.SingleBorderWindow;
    private ResizeMode _resizeBeforeReading = ResizeMode.CanResize;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsReadMode))
        {
            ApplyReadMode(_viewModel!.IsReadMode);
        }
    }

    private void ApplyReadMode(bool on)
    {
        if (on)
        {
            _stateBeforeReading = WindowState;
            _styleBeforeReading = WindowStyle;
            _resizeBeforeReading = ResizeMode;

            WindowState = WindowState.Normal;      // toggle out first so a borderless maximise fills the screen
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = _styleBeforeReading;
            ResizeMode = _resizeBeforeReading;
            WindowState = _stateBeforeReading;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is not { IsReadMode: true })
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Right or Key.Down or Key.PageDown or Key.Space or Key.Enter:
                _viewModel.TurnReadingPage(1);
                e.Handled = true;
                break;
            case Key.Left or Key.Up or Key.PageUp or Key.Back:
                _viewModel.TurnReadingPage(-1);
                e.Handled = true;
                break;
            case Key.Home:
                _viewModel.SelectedTab?.LeftPane.GoToPage(1);
                e.Handled = true;
                break;
            case Key.End:
                _viewModel.SelectedTab?.LeftPane.GoToPage(int.MaxValue);
                e.Handled = true;
                break;
        }
    }

    private void OnWindowClosing(object sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && !viewModel.ConfirmShutdown())
        {
            e.Cancel = true;
        }
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
