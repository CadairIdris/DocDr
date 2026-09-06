using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DocDr.App.ViewModels;

namespace DocDr.App.Views;

public partial class DocumentTabView : UserControl
{
    // The last width the nav column had while open (Auto, or a pixel width the user dragged to).
    private GridLength _navWidth = GridLength.Auto;
    private DocumentTabViewModel? _viewModel;

    public DocumentTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as DocumentTabViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            // This view instance is reused across tabs; sync the column to the new tab's state.
            _navWidth = GridLength.Auto;
            ApplyNavColumn(_viewModel.IsNavigationPanelVisible);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentTabViewModel.IsNavigationPanelVisible) && _viewModel is not null)
        {
            ApplyNavColumn(_viewModel.IsNavigationPanelVisible);
        }
    }

    private void ApplyNavColumn(bool visible)
    {
        if (visible)
        {
            NavColumn.Width = _navWidth;
        }
        else
        {
            // Remember a user-dragged width, then actually collapse the column — an Auto column
            // won't shrink on its own once the GridSplitter has written pixels into it.
            if (NavColumn.Width.IsAbsolute)
            {
                _navWidth = NavColumn.Width;
            }

            NavColumn.Width = new GridLength(0);
        }

        // The PreviousAndNext splitter also pins the main column to pixels on drag; keep it filling.
        MainColumn.Width = new GridLength(1, GridUnitType.Star);
    }
}
