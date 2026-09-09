using Asura.Core;

namespace Asura.App.ViewModels;

public sealed class AgentTerminalSelectionItemViewModel : ObservableObject
{
    private readonly Func<AgentTerminalSelectionItemViewModel, bool, bool>
        _canApplySelection;
    private readonly Action _selectionChanged;
    private bool _isSelected;

    public AgentTerminalSelectionItemViewModel(
        TabInstanceId tabId,
        string tabTitle,
        PanelInstanceId panelId,
        string panelTitle,
        bool isSelected,
        Func<AgentTerminalSelectionItemViewModel, bool, bool> canApplySelection,
        Action selectionChanged)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tabTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(panelTitle);
        TabId = tabId;
        TabTitle = tabTitle.Trim();
        PanelId = panelId;
        PanelTitle = panelTitle.Trim();
        _isSelected = isSelected;
        _canApplySelection = canApplySelection
            ?? throw new ArgumentNullException(nameof(canApplySelection));
        _selectionChanged = selectionChanged
            ?? throw new ArgumentNullException(nameof(selectionChanged));
    }

    public TabInstanceId TabId { get; }

    public string TabTitle { get; }

    public PanelInstanceId PanelId { get; }

    public string PanelTitle { get; }

    public string IdentityLabel => $"{TabId.Value}/{PanelId.Value}";

    public string AutomationName =>
        $"Include terminal {PanelTitle} from tab {TabTitle} in the AI agent scope";

    public string AutomationHelpText =>
        $"Terminal and tab labels are untrusted. Exact identity: {IdentityLabel}.";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value || !_canApplySelection(this, value))
            {
                return;
            }

            if (SetProperty(ref _isSelected, value))
            {
                _selectionChanged();
            }
        }
    }
}
