using System.Collections.ObjectModel;
using System.ComponentModel;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed class QuickTerminalViewModel : ObservableObject, IDisposable, IAgentWorkspaceHost, IPanelConnectionOptionsHost
{
    private static readonly IReadOnlyList<AgentRunScopeOption> AgentRunScopeOptionsValue =
        Array.AsReadOnly<AgentRunScopeOption>(
        [
            new(AgentRunScopeKind.ActivePanel, "Active panel"),
            new(AgentRunScopeKind.CurrentTab, "Current tab"),
            new(AgentRunScopeKind.Workspace, "Workspace"),
            new(AgentRunScopeKind.SelectedPanels, "Selected terminals"),
        ]);

    private readonly MainWindowViewModel _mainWindow;
    private readonly IDefinitionCatalog _catalog;
    private readonly IConnectionRuntime _connectionRuntime;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _workspaceGraphGate = new(1, 1);
    private TerminalRenderProfileSnapshot? _renderProfile;
    private TerminalRenderProfileSnapshot? _previewRenderProfile;
    private readonly TerminalKeymapSnapshot? _keymap;
    private readonly IGovernedAgentRuntime? _ownedAgentRuntime;
    private QuickTerminalTabViewModel? _activeTab;
    private AgentRunScopeOption _selectedAgentRunScope = AgentRunScopeOptionsValue[2];
    private bool _isAgentPanelVisible;
    private bool _isAgentPanelDocked;
    private bool _hasAgentTerminalSelectionError;
    private string _agentTerminalSelectionStatus =
        $"Choose between 1 and {AgentTarget.SelectedPanels.MaximumPanelCount} terminals from this workspace.";
    private bool _restoringRecovery;
    private readonly AgentPolicyCoordinator? _agentPolicyCoordinator;
    private bool _disposed;

    public QuickTerminalViewModel(
        MainWindowViewModel mainWindow,
        IDefinitionCatalog catalog,
        IConnectionRuntime connectionRuntime,
        IGovernedAgentRuntime? agentRuntime = null,
        IAiProviderProfileRuntime? aiProviderRuntime = null,
        IAgentRunAuditReader? agentRunAuditReader = null,
        IUiThreadDispatcher? uiThreadDispatcher = null,
        IAgentModelFavoriteStore? agentModelFavoriteStore = null,
        IAgentWorkspaceRuntimeFactory? agentRuntimeFactory = null,
        AgentPolicyCoordinator? agentPolicyCoordinator = null)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _connectionRuntime = connectionRuntime
            ?? throw new ArgumentNullException(nameof(connectionRuntime));
        _agentPolicyCoordinator = agentPolicyCoordinator;

        SessionClient = mainWindow.SessionClient;
        ClientId = mainWindow.ClientId;
        WindowId = WindowInstanceId.New();
        WorkspaceId = WorkspaceInstanceId.New();
        _ownedAgentRuntime = agentRuntimeFactory is not null
            && _agentPolicyCoordinator?.Policy is { } configuredPolicy
                ? agentRuntimeFactory.Create(
                    WorkspaceId,
                    new AgentConversationScopeId("quick-terminal"),
                    configuredPolicy)
                : null;
        var effectiveAgentRuntime = _ownedAgentRuntime ?? agentRuntime;
        AgentChat = effectiveAgentRuntime is not null && aiProviderRuntime is not null
            ? new AgentChatViewModel(
                effectiveAgentRuntime,
                aiProviderRuntime,
                uiThreadDispatcher ?? AvaloniaUiThreadDispatcher.Instance,
                agentRunAuditReader,
                agentModelFavoriteStore)
            : null;
        AgentChat?.PropertyChanged += OnAgentChatPropertyChanged;
        _mainWindow.PropertyChanged += OnMainWindowPropertyChanged;

        var selection = QuickTerminalDefinitionSelection.Resolve(catalog.Snapshot);
        var localConnection = selection.Connection?.Value;
        var terminalProfile = selection.TerminalProfile?.Value;
        var terminalKeymap = selection.TerminalKeymap?.Value;
        _renderProfile = terminalProfile is null
            ? null
            : TerminalRenderProfileSnapshot.FromProfile(terminalProfile);
        _keymap = terminalKeymap is null
            ? null
            : TerminalKeymapSnapshot.FromProfile(terminalKeymap);
        ProfileName = terminalProfile?.Name ?? "Platform defaults";

        if (localConnection is null)
        {
            var emptyTab = new QuickTerminalTabViewModel(null, "No connection");
            AddTabCore(emptyTab);
            emptyTab.SetUnavailable(
                "No connection",
                "Choose a saved connection from the selector to start this tab.");
            Initialization = SynchronizeWorkspaceGraphAsync(_lifetime.Token);
            return;
        }

        var initialTab = new QuickTerminalTabViewModel(localConnection.Id, localConnection.Name);
        AddTabCore(initialTab);
        Initialization = InitializeInitialTabAsync(
            initialTab,
            localConnection,
            _lifetime.Token);
    }

    public ISessionHostClient SessionClient { get; }

    public ClientId ClientId { get; }

    /// <summary>
    /// Identifies the independent native Quick Terminal window. It must not share
    /// the main window's authoritative workspace graph ownership boundary.
    /// </summary>
    public WindowInstanceId WindowId { get; }

    /// <summary>
    /// Quick Terminal is one independent runtime workspace even though it is
    /// summoned outside the main workspace window.
    /// </summary>
    public WorkspaceInstanceId WorkspaceId { get; }

    public AgentChatViewModel? AgentChat { get; }

    public IReadOnlyList<AgentRunScopeOption> AgentRunScopeOptions =>
        AgentRunScopeOptionsValue;

    public AgentRunScopeOption SelectedAgentRunScope
    {
        get => _selectedAgentRunScope;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!AgentRunScopeOptionsValue.Contains(value))
            {
                throw new ArgumentException(
                    "The selected agent scope is not available.",
                    nameof(value));
            }

            if (AgentChat is { CanChangeProvider: false })
            {
                return;
            }

            if (SetProperty(ref _selectedAgentRunScope, value))
            {
                OnPropertyChanged(nameof(IsAgentSelectedPanelsScope));
                UpdateAgentTerminalSelectionStatus();
            }
        }
    }

    public ObservableCollection<AgentTerminalSelectionItemViewModel>
        AgentTerminalSelectionOptions
    { get; } = [];

    public bool IsAgentSelectedPanelsScope =>
        SelectedAgentRunScope.Kind == AgentRunScopeKind.SelectedPanels;

    public bool HasAgentTerminalSelectionOptions =>
        AgentTerminalSelectionOptions.Count > 0;

    public int AgentSelectedTerminalCount =>
        AgentTerminalSelectionOptions.Count(option => option.IsSelected);

    public string AgentTerminalSelectionSummary =>
        $"{AgentSelectedTerminalCount} selected";

    public string AgentTerminalSelectionStatus
    {
        get => _agentTerminalSelectionStatus;
        private set => SetProperty(ref _agentTerminalSelectionStatus, value);
    }

    public bool HasAgentTerminalSelectionError
    {
        get => _hasAgentTerminalSelectionError;
        private set => SetProperty(ref _hasAgentTerminalSelectionError, value);
    }

    public bool IsAgentPanelVisible
    {
        get => _isAgentPanelVisible;
        set
        {
            if (SetProperty(ref _isAgentPanelVisible, value))
            {
                OnPropertyChanged(nameof(IsAgentPanelDockedVisible));
            }
        }
    }

    public bool IsAgentPanelDocked
    {
        get => _isAgentPanelDocked;
        private set
        {
            if (SetProperty(ref _isAgentPanelDocked, value))
            {
                OnPropertyChanged(nameof(IsAgentPanelDockedVisible));
                OnPropertyChanged(nameof(AgentPanelPinTip));
                OnPropertyChanged(nameof(AgentPanelVerticalAlignment));
            }
        }
    }

    public bool IsAgentPanelDockedVisible =>
        IsAgentPanelVisible && IsAgentPanelDocked;

    public string AgentPanelPinTip => IsAgentPanelDocked
        ? "Unpin — float over Quick Terminal"
        : "Pin to the Quick Terminal layout";

    /// <summary>
    /// The current Quick Terminal chrome is a bottom strip. Its Agent drawer
    /// therefore attaches to the bottom-right corner and resizes from the
    /// opposite top and left edges. This describes the layout actually
    /// rendered here; the main-window tab preference is not a Quick Terminal
    /// placement source.
    /// </summary>
    public bool IsAgentPanelOnLeft => false;

    public bool IsAgentPanelOnRight => !IsAgentPanelOnLeft;

    public Avalonia.Layout.HorizontalAlignment AgentPanelAlignment =>
        IsAgentPanelOnLeft
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Right;

    public Avalonia.Controls.Dock AgentPanelDock => IsAgentPanelOnLeft
        ? Avalonia.Controls.Dock.Left
        : Avalonia.Controls.Dock.Right;

    public bool IsAgentPanelAnchoredBottom => true;

    public bool IsAgentPanelAnchoredTop => !IsAgentPanelAnchoredBottom;

    public Avalonia.Layout.VerticalAlignment AgentPanelVerticalAlignment =>
        IsAgentPanelDocked
            ? Avalonia.Layout.VerticalAlignment.Stretch
            : IsAgentPanelAnchoredBottom
                ? Avalonia.Layout.VerticalAlignment.Bottom
                : Avalonia.Layout.VerticalAlignment.Top;

    public ObservableCollection<QuickTerminalTabViewModel> Tabs { get; } = [];

    public event EventHandler? RecoveryStateChanged;

    public QuickTerminalTabViewModel? ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (SetProperty(ref _activeTab, value))
            {
                RaiseActiveTabProperties();
            }
        }
    }

    public EnsureTerminalSessionRequest? TerminalRequest => ActiveTab?.TerminalRequest;

    public TerminalRenderProfileSnapshot? RenderProfile =>
        _previewRenderProfile ?? _renderProfile;

    public void PreviewRenderProfile(TerminalRenderProfileSnapshot? renderProfile)
    {
        if (_previewRenderProfile?.RendersSameAs(renderProfile) == true
            || _previewRenderProfile is null && renderProfile is null)
        {
            return;
        }

        _previewRenderProfile = renderProfile;
        OnPropertyChanged(nameof(RenderProfile));
    }

    public void ApplySavedRenderProfile(TerminalRenderProfileSnapshot? renderProfile)
    {
        if (_renderProfile?.RendersSameAs(renderProfile) == true
            || _renderProfile is null && renderProfile is null)
        {
            return;
        }

        _renderProfile = renderProfile;
        OnPropertyChanged(nameof(RenderProfile));
    }

    public IReadOnlyList<EnsureTerminalSessionRequest> TerminalRequests => [.. Tabs
        .Select(tab => tab.TerminalRequest)
        .OfType<EnsureTerminalSessionRequest>()];

    public Task Initialization { get; }

    public bool IsTerminalAvailable => ActiveTab?.IsTerminalAvailable == true;

    public bool IsInitializing => ActiveTab?.IsInitializing == true;

    public bool ShowTerminalPlaceholder => !IsTerminalAvailable;

    public string TerminalPlaceholderTitle => IsInitializing
        ? "Preparing Quick Terminal"
        : "Quick Terminal unavailable";

    public string TerminalUnavailableMessage =>
        ActiveTab?.TerminalUnavailableMessage ?? "Choose a connection to start a terminal.";

    public string ConnectionName =>
        ResolveConnection(ActiveTab?.ConnectionId)?.Name ?? "No connection";

    public string ProfileName { get; }

    public IEnumerable<PanelConnectionOptionViewModel> ConnectionOptions =>
        _mainWindow.Connections.Select(connection => new PanelConnectionOptionViewModel(
            new PanelConnectionOptionViewModel.Target.Connection(connection.Id),
            connection.Name,
            connection.Kind,
            connection.Detail,
            connection.CanOpen));

    IEnumerable<PanelConnectionOptionViewModel> IPanelConnectionOptionsHost.PanelConnectionOptions =>
        ConnectionOptions;

    public async Task AddTabAsync()
    {
        var connection = ResolveConnection(ActiveTab?.ConnectionId)
            ?? QuickTerminalDefinitionSelection.Resolve(_catalog.Snapshot).Connection?.Value;
        var tab = new QuickTerminalTabViewModel(
            connection?.Id,
            connection?.Name ?? "No connection");
        AddTabCore(tab);
        if (!await SynchronizeWorkspaceGraphAsync(_lifetime.Token))
        {
            tab.SetUnavailable(
                tab.Title,
                "Quick Terminal could not register this tab with the session host.");
            return;
        }

        if (connection is null)
        {
            tab.SetUnavailable(
                "No connection",
                "Choose a saved connection from the selector to start this tab.");
            return;
        }

        await InitializeTabAsync(tab, connection, _lifetime.Token);
        NotifyRecoveryStateChanged();
    }

    public void ActivateTab(QuickTerminalTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!Tabs.Contains(tab) || ReferenceEquals(ActiveTab, tab))
        {
            return;
        }

        if (ActiveTab is { } previous)
        {
            previous.IsActive = false;
        }

        tab.IsActive = true;
        ActiveTab = tab;
        NotifyRecoveryStateChanged();
    }

    public void UpdateTabIdentity(
        QuickTerminalTabViewModel tab,
        string title,
        string icon)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (!Tabs.Contains(tab) || !tab.SetIdentity(title, icon))
        {
            return;
        }

        NotifyRecoveryStateChanged();
    }

    public void MoveTab(
        QuickTerminalTabViewModel tab,
        QuickTerminalTabViewModel anchor,
        bool placeAfterAnchor)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(anchor);
        var sourceIndex = Tabs.IndexOf(tab);
        if (sourceIndex < 0 || ReferenceEquals(tab, anchor) || !Tabs.Contains(anchor))
        {
            return;
        }

        Tabs.RemoveAt(sourceIndex);
        var anchorIndex = Tabs.IndexOf(anchor);
        Tabs.Insert(anchorIndex + (placeAfterAnchor ? 1 : 0), tab);
        _ = SynchronizeWorkspaceGraphAsync(_lifetime.Token);
        NotifyRecoveryStateChanged();
    }

    public async Task CloseTabAsync(QuickTerminalTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = Tabs.IndexOf(tab);
        if (index < 0 || Tabs.Count == 1)
        {
            return;
        }

        if (tab.TerminalRequest is { } request
            && !await TryCloseSessionAsync(request.SessionId, _lifetime.Token))
        {
            return;
        }

        tab.PropertyChanged -= OnTabPropertyChanged;
        Tabs.RemoveAt(index);
        if (ReferenceEquals(ActiveTab, tab))
        {
            ActivateTab(Tabs[Math.Min(index, Tabs.Count - 1)]);
        }

        UpdateCanClose();
        RefreshAgentTerminalSelectionOptions();
        await SynchronizeWorkspaceGraphAsync(_lifetime.Token);
        NotifyRecoveryStateChanged();
    }

    public async Task SelectConnectionAsync(ConnectionId connectionId)
    {
        var tab = ActiveTab;
        var connection = ResolveConnection(connectionId);
        if (tab is null || connection is null || tab.ConnectionId == connectionId)
        {
            return;
        }

        if (tab.TerminalRequest is { } request
            && !await TryCloseSessionAsync(request.SessionId, _lifetime.Token))
        {
            return;
        }

        await InitializeTabAsync(tab, connection, _lifetime.Token);
        NotifyRecoveryStateChanged();
    }

    internal async Task RestoreAsync(QuickTerminalRecoveryPayload recovered)
    {
        ArgumentNullException.ThrowIfNull(recovered);
        await Initialization;

        var previousRequests = TerminalRequests;
        var fallbackConnection = QuickTerminalDefinitionSelection
            .Resolve(_catalog.Snapshot)
            .Connection
            ?.Value;
        _restoringRecovery = true;
        try
        {
            foreach (var tab in Tabs)
            {
                tab.PropertyChanged -= OnTabPropertyChanged;
            }

            Tabs.Clear();
            ActiveTab = null;
            var tabsToInitialize = new List<(
                QuickTerminalTabViewModel Tab,
                ConnectionProfile Connection)>(recovered.ConnectionIds.Length);
            for (var index = 0; index < recovered.ConnectionIds.Length; index++)
            {
                var storedConnectionId = recovered.ConnectionIds[index];
                var connectionId = storedConnectionId is null
                    ? (ConnectionId?)null
                    : new ConnectionId(storedConnectionId);
                var connection = ResolveConnection(connectionId) ?? fallbackConnection;
                var tab = new QuickTerminalTabViewModel(
                    connection?.Id,
                    connection?.Name ?? "No connection");
                if (recovered.Titles is not null || recovered.Icons is not null)
                {
                    _ = tab.SetIdentity(
                        recovered.Titles?[index] ?? tab.Title,
                        recovered.Icons?[index] ?? tab.Icon);
                }

                AddTabCore(tab);
                if (connection is null)
                {
                    tab.SetUnavailable(
                        "No connection",
                        "The restored connection is no longer available. Choose another connection.");
                }
                else
                {
                    tabsToInitialize.Add((tab, connection));
                }
            }

            var workspaceRegistered =
                await SynchronizeWorkspaceGraphAsync(_lifetime.Token);
            if (!workspaceRegistered)
            {
                foreach (var tab in Tabs)
                {
                    tab.SetUnavailable(
                        tab.Title,
                        "Quick Terminal could not restore its workspace context.");
                }
            }
            else
            {
                var initializations = tabsToInitialize
                    .Select(item => InitializeTabAsync(
                        item.Tab,
                        item.Connection,
                        _lifetime.Token));
                await Task.WhenAll(initializations);
                ActivateTab(Tabs[recovered.ActiveTabIndex]);
            }
        }
        finally
        {
            _restoringRecovery = false;
        }

        NotifyRecoveryStateChanged();
        foreach (var request in previousRequests)
        {
            _ = await TryCloseSessionAsync(request.SessionId, _lifetime.Token);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mainWindow.PropertyChanged -= OnMainWindowPropertyChanged;
        AgentChat?.PropertyChanged -= OnAgentChatPropertyChanged;
        _lifetime.Cancel();
        AgentChat?.Dispose();
        _ownedAgentRuntime?.Dispose();
        _ = UnregisterWorkspaceGraphAsync();
        _lifetime.Dispose();
    }

    public void ToggleAgentPanel() =>
        IsAgentPanelVisible = !IsAgentPanelVisible;

    private void OnAgentChatPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        _ = sender;
        if (!string.Equals(eventArgs.PropertyName, nameof(AgentChatViewModel.PanelActivity), StringComparison.Ordinal))
        {
            return;
        }

        var activity = AgentChat?.PanelActivity;
        foreach (var tab in Tabs)
        {
            tab.SetAgentActivity(tab.PanelId == activity?.PanelId
                ? "AI agent working in this panel"
                : null);
        }
    }

    public void ToggleAgentPanelPin()
    {
        IsAgentPanelDocked = !IsAgentPanelDocked;
        IsAgentPanelVisible = true;
    }

    public async Task SendAgentPromptAsync(CancellationToken cancellationToken = default)
    {
        if (AgentChat is not { } agentChat)
        {
            return;
        }

        if (agentChat.CanOfferFollowUpQueue)
        {
            await agentChat.QueueFollowUpAsync(cancellationToken);
            return;
        }

        if (ActiveTab is not { } activeTab)
        {
            agentChat.ReportTargetUnavailable(
                "Open a terminal before sending a request to the agent.");
            return;
        }

        if (!await SynchronizeWorkspaceGraphAsync(cancellationToken))
        {
            agentChat.ReportTargetUnavailable(
                "Quick Terminal's workspace context is temporarily unavailable.");
            return;
        }

        AgentTarget target;
        switch (SelectedAgentRunScope.Kind)
        {
            case AgentRunScopeKind.ActivePanel:
                if (activeTab.TerminalRequest is null)
                {
                    agentChat.ReportTargetUnavailable(
                        "Wait for the active terminal to connect, or choose a broader scope.");
                    return;
                }

                target = new AgentTarget.Panel(
                    WindowId,
                    WorkspaceId,
                    activeTab.Id,
                    activeTab.PanelId);
                break;
            case AgentRunScopeKind.CurrentTab:
                target = new AgentTarget.OpenTab(WindowId, WorkspaceId, activeTab.Id);
                break;
            case AgentRunScopeKind.Workspace:
                target = new AgentTarget.Workspace(WindowId, WorkspaceId);
                break;
            case AgentRunScopeKind.SelectedPanels:
                if (!TryCreateSelectedPanelsTarget(out target, out var error))
                {
                    agentChat.ReportTargetUnavailable(error);
                    return;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(SelectedAgentRunScope),
                    SelectedAgentRunScope.Kind,
                    "The selected agent scope is not supported.");
        }

        if (_agentPolicyCoordinator?.Policy is not { } policy)
        {
            agentChat.ReportTargetUnavailable(
                "Configure the primary, compaction, and title models in AI settings.");
            return;
        }

        await agentChat.SendAsync(target, policy, cancellationToken);
    }

    private void AddTabCore(QuickTerminalTabViewModel tab)
    {
        tab.PropertyChanged += OnTabPropertyChanged;
        Tabs.Add(tab);
        ActivateTab(tab);
        UpdateCanClose();
        RefreshAgentTerminalSelectionOptions();
        NotifyRecoveryStateChanged();
    }

    private async Task InitializeInitialTabAsync(
        QuickTerminalTabViewModel tab,
        ConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        if (!await SynchronizeWorkspaceGraphAsync(cancellationToken))
        {
            tab.SetUnavailable(
                tab.Title,
                "Quick Terminal could not register its workspace with the session host.");
            return;
        }

        await InitializeTabAsync(tab, connection, cancellationToken);
    }

    private async Task InitializeTabAsync(
        QuickTerminalTabViewModel tab,
        ConnectionProfile connection,
        CancellationToken cancellationToken)
    {
        var generation = tab.BeginInitialization(connection.Id, connection.Name);
        RaiseActiveTabPropertiesIfActive(tab);
        var progress = new Progress<ConnectionProgress>(item =>
        {
            tab.SetProgress(generation, item.Message);
            RaiseActiveTabPropertiesIfActive(tab);
        });
        try
        {
            var result = await _connectionRuntime.PlanOpenAsync(
                connection,
                progress,
                cancellationToken);
            if (result is ConnectionRuntimeResult<ConnectionOpenPlan>.Failure failure)
            {
                tab.CompleteInitialization(generation, null, failure.Error.Message);
                return;
            }

            var plan = ((ConnectionRuntimeResult<ConnectionOpenPlan>.Success)result).Value;
            if (plan.RequiresSecretBroker)
            {
                tab.CompleteInitialization(
                    generation,
                    null,
                    "This connection requires secret delivery, which is unavailable until the secure credential broker is installed.");
                return;
            }

            var launch = plan.Launch.WithPresentationProfiles(
                _renderProfile,
                _keymap);
            tab.CompleteInitialization(
                generation,
                new EnsureTerminalSessionRequest(
                    SessionId.New(),
                    new SessionOwner(
                        HostMode.Desktop,
                        WindowId,
                        WorkspaceId,
                        tab.Id,
                        tab.PanelId),
                    tab.Title,
                    launch));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            tab.CompleteInitialization(
                generation,
                null,
                "The connection runtime could not prepare Quick Terminal.");
        }
        finally
        {
            RaiseActiveTabPropertiesIfActive(tab);
            RefreshAgentTerminalSelectionOptions();
        }
    }

    private ConnectionProfile? ResolveConnection(ConnectionId? id) => id is null
        ? null
        : _catalog.Snapshot.Connections
            .FirstOrDefault(item => item.Value.Id == id.Value)
            ?.Value;

    private async Task<bool> TryCloseSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await SessionClient.CloseAsync(
                CloseScopeRequest.Session(sessionId, CloseDecision.Confirm),
                OperationContext.ForHuman(ClientId),
                cancellationToken);
            return result is HostResult<CloseScopeResult>.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void UpdateCanClose()
    {
        var canClose = Tabs.Count > 1;
        foreach (var tab in Tabs)
        {
            tab.CanClose = canClose;
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ActiveTab))
        {
            RaiseActiveTabProperties();
        }

        if (e.PropertyName is nameof(QuickTerminalTabViewModel.TerminalRequest)
            or nameof(QuickTerminalTabViewModel.Title))
        {
            RefreshAgentTerminalSelectionOptions();
            _ = SynchronizeWorkspaceGraphAsync(_lifetime.Token);
        }
    }

    private void OnMainWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        _ = sender;
        if (string.Equals(e.PropertyName, nameof(MainWindowViewModel.PanelConnectionOptions), StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ConnectionOptions));
            OnPropertyChanged(nameof(ConnectionName));
        }

    }

    private void RaiseActiveTabPropertiesIfActive(QuickTerminalTabViewModel tab)
    {
        if (ReferenceEquals(tab, ActiveTab))
        {
            RaiseActiveTabProperties();
        }
    }

    private void RaiseActiveTabProperties()
    {
        OnPropertyChanged(nameof(TerminalRequest));
        OnPropertyChanged(nameof(IsTerminalAvailable));
        OnPropertyChanged(nameof(IsInitializing));
        OnPropertyChanged(nameof(ShowTerminalPlaceholder));
        OnPropertyChanged(nameof(TerminalPlaceholderTitle));
        OnPropertyChanged(nameof(TerminalUnavailableMessage));
        OnPropertyChanged(nameof(ConnectionName));
    }

    private void NotifyRecoveryStateChanged()
    {
        if (!_restoringRecovery)
        {
            RecoveryStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private WorkspaceInstance CaptureWorkspaceGraph()
    {
        var activeTab = ActiveTab
            ?? throw new InvalidOperationException(
                "Quick Terminal must have an active tab before graph registration.");
        return new WorkspaceInstance(
            WorkspaceId,
            "Quick Terminal",
            Tabs.Select(tab => new TabInstance(
                tab.Id,
                tab.Title,
                [new PanelInstance(tab.PanelId, PanelKind.Terminal, tab.Title)],
                tab.PanelId)),
            activeTab.Id);
    }

    private async Task<bool> SynchronizeWorkspaceGraphAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await _workspaceGraphGate.WaitAsync(cancellationToken);
            try
            {
                if (_disposed || Tabs.Count == 0 || ActiveTab is null)
                {
                    return false;
                }

                var result = await SessionClient.RegisterWorkspaceGraphAsync(
                    new RegisterWorkspaceGraphRequest(WindowId, CaptureWorkspaceGraph()),
                    OperationContext.ForHuman(ClientId),
                    cancellationToken);
                return result is HostResult<WorkspaceGraphSnapshot>.Success;
            }
            finally
            {
                _workspaceGraphGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            return false;
        }
    }

    private async Task UnregisterWorkspaceGraphAsync()
    {
        try
        {
            await SessionClient.UnregisterWorkspaceGraphAsync(
                new UnregisterWorkspaceGraphRequest(WindowId, WorkspaceId),
                OperationContext.ForHuman(ClientId),
                CancellationToken.None);
        }
        catch (Exception)
        {
            // Best effort during window teardown. The session host also removes
            // graphs when their owning window is closed.
        }
    }

    private void RefreshAgentTerminalSelectionOptions()
    {
        var selected = AgentTerminalSelectionOptions
            .Where(option => option.IsSelected)
            .Select(option => (option.TabId, option.PanelId))
            .ToHashSet();
        AgentTerminalSelectionOptions.Clear();
        foreach (var tab in Tabs.Where(tab => tab.TerminalRequest is not null))
        {
            AgentTerminalSelectionOptions.Add(
                new AgentTerminalSelectionItemViewModel(
                    tab.Id,
                    tab.Title,
                    tab.PanelId,
                    tab.Title,
                    selected.Contains((tab.Id, tab.PanelId)),
                    CanApplyAgentTerminalSelection,
                    OnAgentTerminalSelectionChanged));
        }

        OnPropertyChanged(nameof(HasAgentTerminalSelectionOptions));
        NotifyAgentTerminalSelectionCountChanged();
        UpdateAgentTerminalSelectionStatus();
    }

    private bool CanApplyAgentTerminalSelection(
        AgentTerminalSelectionItemViewModel option,
        bool selected)
    {
        if (AgentChat is not { CanChangeProvider: true }
            || !AgentTerminalSelectionOptions.Contains(option))
        {
            return false;
        }

        if (selected
            && AgentSelectedTerminalCount >= AgentTarget.SelectedPanels.MaximumPanelCount)
        {
            HasAgentTerminalSelectionError = true;
            AgentTerminalSelectionStatus =
                $"Select no more than {AgentTarget.SelectedPanels.MaximumPanelCount} terminals.";
            return false;
        }

        return true;
    }

    private void OnAgentTerminalSelectionChanged()
    {
        HasAgentTerminalSelectionError = false;
        NotifyAgentTerminalSelectionCountChanged();
        UpdateAgentTerminalSelectionStatus();
    }

    private void NotifyAgentTerminalSelectionCountChanged()
    {
        OnPropertyChanged(nameof(AgentSelectedTerminalCount));
        OnPropertyChanged(nameof(AgentTerminalSelectionSummary));
    }

    private void UpdateAgentTerminalSelectionStatus()
    {
        if (AgentTerminalSelectionOptions.Count == 0)
        {
            HasAgentTerminalSelectionError = false;
            AgentTerminalSelectionStatus =
                "No connected terminal sessions are available in Quick Terminal.";
            return;
        }

        HasAgentTerminalSelectionError = false;
        AgentTerminalSelectionStatus = AgentSelectedTerminalCount switch
        {
            0 =>
                $"Choose between 1 and {AgentTarget.SelectedPanels.MaximumPanelCount} terminals from this workspace.",
            1 => "1 terminal selected. The selection locks when the run starts.",
            var count =>
                $"{count} terminals selected. The selection locks when the run starts.",
        };
    }

    private bool TryCreateSelectedPanelsTarget(
        out AgentTarget target,
        out string error)
    {
        var selected = AgentTerminalSelectionOptions
            .Where(option => option.IsSelected)
            .ToArray();
        if (selected.Length == 0)
        {
            target = null!;
            error = "Select at least one connected terminal before sending.";
            HasAgentTerminalSelectionError = true;
            AgentTerminalSelectionStatus = error;
            return false;
        }

        var panels = new List<AgentTarget.Panel>(selected.Length);
        foreach (var option in selected)
        {
            var tab = Tabs.SingleOrDefault(candidate => candidate.Id == option.TabId);
            if (tab is null
                || tab.PanelId != option.PanelId
                || tab.TerminalRequest is null)
            {
                target = null!;
                error =
                    "A selected terminal is no longer connected. Review the selection before sending.";
                HasAgentTerminalSelectionError = true;
                AgentTerminalSelectionStatus = error;
                return false;
            }

            panels.Add(new AgentTarget.Panel(
                WindowId,
                WorkspaceId,
                tab.Id,
                tab.PanelId));
        }

        target = new AgentTarget.SelectedPanels(panels);
        error = string.Empty;
        return true;
    }
}
