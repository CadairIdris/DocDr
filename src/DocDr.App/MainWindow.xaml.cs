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
            ApplyGroupLayout();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsReadMode))
        {
            ApplyReadMode(_viewModel!.IsReadMode);
        }
        else if (e.PropertyName is nameof(MainViewModel.ShowLeftGroup) or nameof(MainViewModel.ShowRightGroup))
        {
            ApplyGroupLayout();
        }
    }

    // --- Side-by-side tab groups -----------------------------------------------------------

    private GridLength _leftGroupWidth = new(1, GridUnitType.Star);
    private GridLength _rightGroupWidth = new(1, GridUnitType.Star);

    /// <summary>Size the two docked sides. Both showing → the widths they last had (the splitter
    /// edits these star values); one side → it takes the whole area. Remembers the split so docking
    /// again restores it.</summary>
    private void ApplyGroupLayout()
    {
        if (_viewModel is null)
        {
            return;
        }

        bool left = _viewModel.ShowLeftGroup, right = _viewModel.ShowRightGroup;
        if (LeftGroupColumn.Width.Value > 0 && RightGroupColumn.Width.Value > 0)
        {
            _leftGroupWidth = LeftGroupColumn.Width;   // keep whatever the splitter left us
            _rightGroupWidth = RightGroupColumn.Width;
        }

        LeftGroupColumn.Width = left ? (right ? _leftGroupWidth : new GridLength(1, GridUnitType.Star)) : new GridLength(0);
        RightGroupColumn.Width = right ? (left ? _rightGroupWidth : new GridLength(1, GridUnitType.Star)) : new GridLength(0);
        GroupSplitter.Visibility = left && right ? Visibility.Visible : Visibility.Collapsed;
        LeftGroupHost.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        RightGroupHost.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Clicking anywhere in a side makes it the one the toolbar acts on.</summary>
    private void GroupHost_PreviewMouseDown(object sender, MouseButtonEventArgs e) => ActivateGroupOf(sender);

    /// <summary>…as does tabbing / keyboard focus moving into it.</summary>
    private void GroupHost_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ActivateGroupOf(sender);

    private void ActivateGroupOf(object sender)
    {
        if (_viewModel is not null && sender is FrameworkElement { DataContext: TabGroupViewModel group })
        {
            _viewModel.ActivateGroup(group);
        }
    }

    // Tab drag: press on a tab header, move past the system drag threshold, and the tab is lifted;
    // the drop zone for the other side lights up (the right half of the window when nothing is
    // docked there yet), and dropping in it docks the tab there.
    private const string TabDragFormat = "DocDr.DocumentTab";
    private Point? _tabDragStart;
    private DocumentTabViewModel? _tabDragCandidate;
    private TabGroupViewModel? _tabDragTarget;
    private Rect _tabDropZone;

    private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Not from the ✕ button — that's a click, never a drag.
        if (FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        _tabDragStart = e.GetPosition(this);
        _tabDragCandidate = (sender as FrameworkElement)?.DataContext as DocumentTabViewModel;
    }

    private void TabItem_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = null;
        _tabDragCandidate = null;
    }

    private void TabItem_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _tabDragStart is not { } start
            || _tabDragCandidate is not { } tab || _viewModel is null || sender is not FrameworkElement header)
        {
            return;
        }

        Vector moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _tabDragStart = null;
        _tabDragCandidate = null;

        TabGroupViewModel? source = _viewModel.GroupOf(tab);
        if (source is null)
        {
            return;
        }

        TabGroupViewModel target = source.IsPrimary ? _viewModel.RightGroup : _viewModel.LeftGroup;
        if (!_viewModel.CanMoveTab(tab, target))
        {
            return; // e.g. the only open tab — nothing to put beside it
        }

        BeginTabDrag(header, tab, target);
    }

    private void BeginTabDrag(FrameworkElement header, DocumentTabViewModel tab, TabGroupViewModel target)
    {
        _tabDragTarget = target;
        _tabDropZone = DropZoneFor(target);
        System.Windows.Controls.Canvas.SetLeft(TabDropZone, _tabDropZone.X);
        System.Windows.Controls.Canvas.SetTop(TabDropZone, _tabDropZone.Y);
        TabDropZone.Width = _tabDropZone.Width;
        TabDropZone.Height = _tabDropZone.Height;
        TabDropZoneText.Text = target.HasTabs
            ? (target.IsPrimary ? "Move to the left side" : "Move to the right side")
            : "Drop to view side by side";
        TabDropZoneFill.Opacity = 0.15;
        TabDropOverlay.Visibility = Visibility.Visible;

        try
        {
            DragDrop.DoDragDrop(header, new DataObject(TabDragFormat, tab), DragDropEffects.Move);
        }
        finally
        {
            TabDropOverlay.Visibility = Visibility.Collapsed;
            _tabDragTarget = null;
        }
    }

    /// <summary>Where a tab can land, in <see cref="TabArea"/> coordinates: the other side's area
    /// when two are docked, else the right half of the window (which becomes the right side).</summary>
    private Rect DropZoneFor(TabGroupViewModel target)
    {
        const double inset = 6;
        Rect zone;
        if (target.HasTabs)
        {
            FrameworkElement host = target.IsPrimary ? LeftGroupHost : RightGroupHost;
            Point origin = host.TranslatePoint(new Point(0, 0), TabArea);
            zone = new Rect(origin, new Size(host.ActualWidth, host.ActualHeight));
        }
        else
        {
            double half = TabArea.ActualWidth / 2;
            zone = new Rect(half, 0, TabArea.ActualWidth - half, TabArea.ActualHeight);
        }

        zone.Inflate(-inset, -inset);
        return zone.Width > 0 && zone.Height > 0 ? zone : Rect.Empty;
    }

    private bool IsOverDropZone(DragEventArgs e) =>
        _tabDragTarget is not null && e.Data.GetDataPresent(TabDragFormat)
        && _tabDropZone.Contains(e.GetPosition(TabDropOverlay));

    private void TabDropOverlay_DragOver(object sender, DragEventArgs e)
    {
        bool over = IsOverDropZone(e);
        e.Effects = over ? DragDropEffects.Move : DragDropEffects.None;
        TabDropZoneFill.Opacity = over ? 0.35 : 0.15;
        e.Handled = true;
    }

    private void TabDropOverlay_DragLeave(object sender, DragEventArgs e) => TabDropZoneFill.Opacity = 0.15;

    private void TabDropOverlay_Drop(object sender, DragEventArgs e)
    {
        if (IsOverDropZone(e) && _viewModel is not null && _tabDragTarget is { } target
            && e.Data.GetData(TabDragFormat) is DocumentTabViewModel tab)
        {
            // After the drag loop unwinds — moving now would pull the dragged header (the drag
            // source) out of the visual tree mid-operation.
            MainViewModel viewModel = _viewModel;
            Dispatcher.BeginInvoke(() => viewModel.MoveTab(tab, target), System.Windows.Threading.DispatcherPriority.Input);
        }

        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? node)
        where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
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

    private void InkColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } || _viewModel?.SelectedTab is not { } tab)
        {
            return;
        }

        if (key == AnnotationColors.Custom)
        {
            if (Views.ColorPickerWindow.Pick(AnnotationColors.CustomColorArgb, this) is not { } picked)
            {
                return;
            }

            tab.ApplyCustomColor(picked);
        }
        else
        {
            tab.InkColorKey = key;
        }

        tab.HighlighterToolActive = true;
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
        if (e.Data.GetDataPresent(TabDragFormat))
        {
            return; // a tab being docked — the tab drop overlay handles it
        }

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
        if (e.Data.GetDataPresent(TabDragFormat))
        {
            return;
        }

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
