using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DocDr.App.ViewModels;

/// <summary>
/// One of the two docked tab strips. The left (primary) group always exists; the right one only
/// has tabs while documents are side by side. Which group is active decides what
/// <see cref="MainViewModel.SelectedTab"/> — and so the whole toolbar — acts on.
/// </summary>
public sealed partial class TabGroupViewModel : ObservableObject
{
    public TabGroupViewModel(bool isPrimary)
    {
        IsPrimary = isPrimary;
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
    }

    /// <summary>True for the left group.</summary>
    public bool IsPrimary { get; }

    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = [];

    public bool HasTabs => Tabs.Count > 0;

    [ObservableProperty]
    private DocumentTabViewModel? _selectedTab;

    /// <summary>The group the user last clicked or focused into.</summary>
    [ObservableProperty]
    private bool _isActive;
}
