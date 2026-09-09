using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns agent run-scope selection and the live terminal identities available to it.
/// Prompt dispatch, policy resolution, approvals, audit, and secrets remain with the host.
/// </summary>
public sealed class AgentWorkspaceScopeViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<AgentRunScopeOption> ScopeOptionsValue =
        Array.AsReadOnly<AgentRunScopeOption>(
        [
            new(AgentRunScopeKind.ActivePanel, "Active panel"),
            new(AgentRunScopeKind.CurrentTab, "Current tab"),
            new(AgentRunScopeKind.Workspace, "Workspace"),
            new(AgentRunScopeKind.SelectedPanels, "Selected terminals"),
        ]);

    private readonly WindowInstanceId _windowId;
    private readonly Func<bool> _canChangeScope;
    private readonly Func<bool> _canChangeTerminalSelection;
    private readonly Action<TerminalRuntimePanelViewModel> _terminalSessionObserved;
    private readonly HashSet<RuntimeTabViewModel> _trackedTabs = [];
    private readonly HashSet<TerminalRuntimePanelViewModel> _trackedTerminals = [];
    private RuntimeWorkspaceViewModel? _workspace;
    private AgentRunScopeOption _selectedScope = ScopeOptionsValue[2];
    private bool _selectionStale;
    private bool _hasSelectionError;
    private string _selectionStatus =
        $"Choose between 1 and {AgentTarget.SelectedPanels.MaximumPanelCount} live terminals from this workspace.";
    private bool _disposed;

    public AgentWorkspaceScopeViewModel(
        WindowInstanceId windowId,
        Func<bool> canChangeScope,
        Func<bool> canChangeTerminalSelection,
        Action<TerminalRuntimePanelViewModel> terminalSessionObserved)
    {
        _windowId = windowId;
        _canChangeScope = canChangeScope
            ?? throw new ArgumentNullException(nameof(canChangeScope));
        _canChangeTerminalSelection = canChangeTerminalSelection
            ?? throw new ArgumentNullException(nameof(canChangeTerminalSelection));
        _terminalSessionObserved = terminalSessionObserved
            ?? throw new ArgumentNullException(nameof(terminalSessionObserved));
    }

    public IReadOnlyList<AgentRunScopeOption> ScopeOptions => ScopeOptionsValue;

    public AgentRunScopeOption SelectedScope
    {
        get => _selectedScope;
        set
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(value);
            if (!ScopeOptionsValue.Contains(value))
            {
                throw new ArgumentException(
                    "The selected agent scope is not available.",
                    nameof(value));
            }

            if (!_canChangeScope() || !SetProperty(ref _selectedScope, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSelectedPanelsScope));
            UpdateSelectionStatus();
        }
    }

    public ObservableCollection<AgentTerminalSelectionItemViewModel> TerminalOptions { get; } = [];

    public bool IsSelectedPanelsScope => SelectedScope.Kind == AgentRunScopeKind.SelectedPanels;

    public bool HasTerminalOptions => TerminalOptions.Count > 0;

    public int SelectedTerminalCount => TerminalOptions.Count(option => option.IsSelected);

    public string SelectionSummary => $"{SelectedTerminalCount} selected";

    public string SelectionStatus
    {
        get => _selectionStatus;
        private set => SetProperty(ref _selectionStatus, value);
    }

    public bool HasSelectionError
    {
        get => _hasSelectionError;
        private set => SetProperty(ref _hasSelectionError, value);
    }

    public void AttachWorkspace(RuntimeWorkspaceViewModel? workspace)
    {
        ThrowIfDisposed();
        StopTracking(_workspace);
        _workspace = workspace;
        StartTracking(workspace);
        RefreshTerminalOptions(resetSelection: true);
    }

    public void StopTracking(RuntimeWorkspaceViewModel? workspace)
    {
        workspace?.Tabs.CollectionChanged -= OnTabsChanged;
        foreach (var tab in _trackedTabs)
        {
            tab.Panels.CollectionChanged -= OnPanelsChanged;
        }

        foreach (var terminal in _trackedTerminals)
        {
            terminal.PropertyChanged -= OnTerminalPropertyChanged;
        }

        _trackedTabs.Clear();
        _trackedTerminals.Clear();
    }

    public void ResetTerminalOptions()
    {
        ThrowIfDisposed();
        RefreshTerminalOptions(resetSelection: true);
    }

    public bool TryCreateTarget(out AgentTarget target, out string error)
    {
        ThrowIfDisposed();
        if (_workspace is not { ActiveTab: { } activeTab } workspace)
        {
            target = null!;
            error =
                "Open a terminal, browser, File Viewer, Statistics, or Process Monitor panel "
                + "before sending a request to the agent.";
            return false;
        }

        switch (SelectedScope.Kind)
        {
            case AgentRunScopeKind.ActivePanel:
                return TryCreateActivePanelTarget(workspace, activeTab, out target, out error);
            case AgentRunScopeKind.CurrentTab:
                target = new AgentTarget.OpenTab(_windowId, workspace.Id, activeTab.Id);
                error = string.Empty;
                return true;
            case AgentRunScopeKind.Workspace:
                target = new AgentTarget.Workspace(_windowId, workspace.Id);
                error = string.Empty;
                return true;
            case AgentRunScopeKind.SelectedPanels:
                return TryCreateSelectedPanelsTarget(workspace, out target, out error);
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(SelectedScope),
                    SelectedScope.Kind,
                    "The selected agent scope is not supported.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopTracking(_workspace);
        _workspace = null;
    }

    private bool TryCreateActivePanelTarget(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel activeTab,
        out AgentTarget target,
        out string error)
    {
        if (activeTab.ActivePanel is not { } activePanel || !IsAgentCapablePanel(activePanel))
        {
            target = null!;
            error =
                "Select an active terminal, browser, File Viewer, hosted Statistics, "
                + "or hosted Process Monitor panel, or choose a broader agent scope.";
            return false;
        }

        target = new AgentTarget.Panel(
            _windowId,
            workspace.Id,
            activeTab.Id,
            activePanel.Id);
        error = string.Empty;
        return true;
    }

    private bool TryCreateSelectedPanelsTarget(
        RuntimeWorkspaceViewModel workspace,
        out AgentTarget target,
        out string error)
    {
        target = null!;
        if (_selectionStale)
        {
            error =
                "A selected terminal is no longer live. Review the selected terminals before sending.";
            SetSelectionError(error, stale: true);
            return false;
        }

        var selected = TerminalOptions.Where(option => option.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            error = "Select at least one live terminal before sending.";
            SetSelectionError(error, stale: false);
            return false;
        }

        if (selected.Length > AgentTarget.SelectedPanels.MaximumPanelCount)
        {
            error =
                $"Select no more than {AgentTarget.SelectedPanels.MaximumPanelCount} terminals.";
            SetSelectionError(error, stale: false);
            return false;
        }

        var panels = new List<AgentTarget.Panel>(selected.Length);
        foreach (var option in selected)
        {
            if (workspace.Tabs.SingleOrDefault(candidate => candidate.Id == option.TabId)
                    is not { } tab
                || tab.Panels.SingleOrDefault(candidate => candidate.Id == option.PanelId)
                    is not TerminalRuntimePanelViewModel terminal
                || !IsLiveAgentTerminal(terminal))
            {
                error =
                    "A selected terminal is no longer live. Review the selected terminals before sending.";
                SetSelectionError(error, stale: true);
                return false;
            }

            panels.Add(new AgentTarget.Panel(_windowId, workspace.Id, tab.Id, terminal.Id));
        }

        target = new AgentTarget.SelectedPanels(panels);
        error = string.Empty;
        HasSelectionError = false;
        UpdateSelectionStatus();
        return true;
    }

    private void StartTracking(RuntimeWorkspaceViewModel? workspace)
    {
        if (workspace is null)
        {
            return;
        }

        workspace.Tabs.CollectionChanged += OnTabsChanged;
        ReconcileSubscriptions(workspace);
    }

    private void ReconcileSubscriptions(RuntimeWorkspaceViewModel workspace)
    {
        foreach (var tab in _trackedTabs)
        {
            tab.Panels.CollectionChanged -= OnPanelsChanged;
        }

        foreach (var terminal in _trackedTerminals)
        {
            terminal.PropertyChanged -= OnTerminalPropertyChanged;
        }

        _trackedTabs.Clear();
        _trackedTerminals.Clear();
        foreach (var tab in workspace.Tabs)
        {
            tab.Panels.CollectionChanged += OnPanelsChanged;
            _trackedTabs.Add(tab);
            foreach (var terminal in tab.Panels.OfType<TerminalRuntimePanelViewModel>())
            {
                terminal.PropertyChanged += OnTerminalPropertyChanged;
                _trackedTerminals.Add(terminal);
            }
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ReconcileAndRefresh();
    }

    private void OnPanelsChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        ReconcileAndRefresh();
    }

    private void ReconcileAndRefresh()
    {
        if (_workspace is not { } workspace)
        {
            return;
        }

        ReconcileSubscriptions(workspace);
        RefreshTerminalOptions(resetSelection: false);
    }

    private void OnTerminalPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (string.Equals(
                eventArgs.PropertyName,
                nameof(TerminalRuntimePanelViewModel.SessionRequest),
                StringComparison.Ordinal)
            && sender is TerminalRuntimePanelViewModel { SessionRequest: not null } terminal)
        {
            _terminalSessionObserved(terminal);
        }

        if (eventArgs.PropertyName is
            nameof(TerminalRuntimePanelViewModel.ConnectionState)
            or nameof(TerminalRuntimePanelViewModel.SessionRequest)
            or nameof(TerminalRuntimePanelViewModel.HasObservedActiveSession))
        {
            RefreshTerminalOptions(resetSelection: false);
        }
    }

    private void RefreshTerminalOptions(bool resetSelection)
    {
        var selected = resetSelection
            ? []
            : TerminalOptions
                .Where(option => option.IsSelected)
                .Select(option => (option.TabId, option.PanelId))
                .ToHashSet();
        var candidates = _workspace?.Tabs
            .SelectMany(tab => tab.Panels
                .OfType<TerminalRuntimePanelViewModel>()
                .Where(IsLiveAgentTerminal)
                .Select(terminal => (Tab: tab, Terminal: terminal)))
            .ToArray()
            ?? [];
        var candidateIds = candidates
            .Select(candidate => (candidate.Tab.Id, candidate.Terminal.Id))
            .ToHashSet();
        var lostSelection = !resetSelection && selected.Any(id => !candidateIds.Contains(id));

        TerminalOptions.Clear();
        foreach (var candidate in candidates)
        {
            TerminalOptions.Add(new AgentTerminalSelectionItemViewModel(
                candidate.Tab.Id,
                candidate.Tab.Title,
                candidate.Terminal.Id,
                candidate.Terminal.Title,
                selected.Contains((candidate.Tab.Id, candidate.Terminal.Id)),
                CanApplyTerminalSelection,
                OnTerminalSelectionChanged));
        }

        if (resetSelection)
        {
            _selectionStale = false;
            HasSelectionError = false;
        }

        if (lostSelection)
        {
            SetSelectionError(
                "A selected terminal is no longer live. Review the selected terminals before sending.",
                stale: true);
        }
        else
        {
            UpdateSelectionStatus();
        }

        OnPropertyChanged(nameof(HasTerminalOptions));
        NotifySelectionCountChanged();
    }

    private bool CanApplyTerminalSelection(
        AgentTerminalSelectionItemViewModel option,
        bool selected)
    {
        if (!_canChangeTerminalSelection() || !TerminalOptions.Contains(option))
        {
            return false;
        }

        if (selected && SelectedTerminalCount >= AgentTarget.SelectedPanels.MaximumPanelCount)
        {
            SetSelectionError(
                $"Select no more than {AgentTarget.SelectedPanels.MaximumPanelCount} terminals.",
                stale: false);
            return false;
        }

        return true;
    }

    private void OnTerminalSelectionChanged()
    {
        _selectionStale = false;
        HasSelectionError = false;
        NotifySelectionCountChanged();
        UpdateSelectionStatus();
    }

    private void NotifySelectionCountChanged()
    {
        OnPropertyChanged(nameof(SelectedTerminalCount));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void UpdateSelectionStatus()
    {
        if (_selectionStale)
        {
            HasSelectionError = true;
            SelectionStatus =
                "A selected terminal is no longer live. Review the selected terminals before sending.";
            return;
        }

        if (TerminalOptions.Count == 0)
        {
            HasSelectionError = false;
            SelectionStatus = "No live terminal sessions are available in this workspace.";
            return;
        }

        HasSelectionError = false;
        SelectionStatus = SelectedTerminalCount switch
        {
            0 =>
                $"Choose between 1 and {AgentTarget.SelectedPanels.MaximumPanelCount} live terminals from this workspace.",
            1 => "1 terminal selected. The selection locks when the run starts.",
            var count =>
                $"{count} terminals selected. The selection locks when the run starts.",
        };
    }

    private void SetSelectionError(string message, bool stale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (stale)
        {
            _selectionStale = true;
        }

        HasSelectionError = true;
        SelectionStatus = message;
    }

    private static bool IsLiveAgentTerminal(TerminalRuntimePanelViewModel terminal) =>
        terminal.ConnectionState == ConnectionPanelState.Ready
        && terminal.SessionRequest is not null
        && terminal.HasObservedActiveSession;

    private static bool IsAgentCapablePanel(RuntimePanelViewModel panel) =>
        panel is TerminalRuntimePanelViewModel
            or BrowserRuntimePanelViewModel
            or FileRuntimePanelViewModel
            or StatisticsRuntimePanelViewModel
            or ProcessMonitorRuntimePanelViewModel { HasHostedSession: true };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
