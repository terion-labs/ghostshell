using System.Collections.ObjectModel;

namespace Asura.App.ViewModels;

/// <summary>The searchable icon choices shown beside a live tab.</summary>
internal sealed class RuntimeTabIconPickerViewModel : ObservableObject
{
    private readonly string _icon;
    private string _iconSearch = string.Empty;
    private bool _showAllIcons;

    public RuntimeTabIconPickerViewModel(string icon)
    {
        _icon = WorkspaceIcons.OptionFor(icon).Id;
        RefreshIconChoices();
    }

    public int IconCount => WorkspaceIcons.All.Count;

    public ObservableCollection<WorkspaceIconChoiceViewModel> IconChoices { get; } = [];

    public string IconSearch
    {
        get => _iconSearch;
        set
        {
            if (SetProperty(ref _iconSearch, value))
            {
                RefreshIconChoices();
            }
        }
    }

    public bool ShowAllIcons
    {
        get => _showAllIcons;
        set
        {
            if (SetProperty(ref _showAllIcons, value))
            {
                RefreshIconChoices();
            }
        }
    }

    public string IconHint => IconChoices.Count == 0
        ? "No icon matches that search."
        : _showAllIcons || !string.IsNullOrWhiteSpace(_iconSearch)
            ? "Pick one, or narrow the search."
            : "Search to reach the rest of the set.";

    private void RefreshIconChoices()
    {
        var current = WorkspaceIcons.OptionFor(_icon);
        var options = string.IsNullOrWhiteSpace(_iconSearch) && !_showAllIcons
            ? WorkspaceIcons.Common.Any(option => string.Equals(option.Id, current.Id, StringComparison.Ordinal))
                ? WorkspaceIcons.Common
                : [current, .. WorkspaceIcons.Common]
            : WorkspaceIcons.Search(_iconSearch);

        IconChoices.Clear();
        foreach (var option in options)
        {
            IconChoices.Add(new WorkspaceIconChoiceViewModel(option)
            {
                IsSelected = string.Equals(option.Id, _icon, StringComparison.Ordinal),
            });
        }

        OnPropertyChanged(nameof(IconHint));
    }
}
