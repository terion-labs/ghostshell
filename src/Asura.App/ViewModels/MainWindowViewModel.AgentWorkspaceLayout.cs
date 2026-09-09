using System.Collections.Immutable;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<WorkspaceInstanceId, MainWindowAgentWorkspaceLayoutPort>
        _agentWorkspaceLayoutPorts = [];

    private IReadOnlySet<PanelKind> SupportedAgentWorkspacePanelKinds()
    {
        var kinds = ImmutableHashSet.CreateBuilder<PanelKind>();
        kinds.Add(PanelKind.Placeholder);
        kinds.Add(PanelKind.FileViewer);
        kinds.Add(PanelKind.Statistics);
        kinds.Add(PanelKind.ProcessMonitor);
        kinds.Add(PanelKind.Terminal);

        if (_browserRendererViewFactory is not null)
        {
            kinds.Add(PanelKind.Browser);
        }

        if (_databasePanelClient is not null)
        {
            kinds.Add(PanelKind.DatabaseViewer);
        }

        if (_dockerEngineClient is not null)
        {
            kinds.Add(PanelKind.Docker);
        }

        return kinds.ToImmutable();
    }

    private async ValueTask<AgentWorkspaceLayoutMutationResult>
        MutateAgentWorkspaceLayoutAsync(
            WorkspaceInstanceId workspaceId,
            AgentWorkspaceLayoutRequest request,
            long expectedWorkspaceRevision,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _uiThreadDispatcher.VerifyAccess();
        var workspace = RuntimeWorkspace;
        if (workspace is null
            || workspace.Id != workspaceId
            || workspace.HostRevision != expectedWorkspaceRevision
            || Overlay is not ShellOverlay.None
            || !RequestTargetsCurrentWorkspace(request, workspace)
            || !RequestKindIsSupported(request)
            || !RequestConnectionIsValid(request, workspace))
        {
            return RejectedLayoutMutation();
        }

        if (RequestClosesUnsavedDatabaseChanges(request, workspace))
        {
            return new AgentWorkspaceLayoutMutationResult.Rejected(
                "workspace_layout_unsaved_changes");
        }

        var before = CaptureRuntimeWorkspaceGraph(workspace);
        if (request is AgentWorkspaceLayoutRequest.ConnectionList)
        {
            var observed = await RuntimeGraph.ObserveWorkspaceAsync(
                workspaceId,
                cancellationToken);
            return observed is not null
                ? new AgentWorkspaceLayoutMutationResult.Observed(
                    observed,
                    _agentWorkspaceLayoutPorts.TryGetValue(
                        workspaceId,
                        out var port)
                        ? port.ListConnections()
                        : [])
                : new AgentWorkspaceLayoutMutationResult.OutcomeUnknown();
        }

        bool changed;
        try
        {
            changed = request switch
            {
                AgentWorkspaceLayoutRequest.TabCreate create =>
                    await CreateAgentWorkspaceTabAsync(
                        workspace,
                        create.Kind,
                        create.ConnectionRef,
                        cancellationToken),
                AgentWorkspaceLayoutRequest.TabClose close =>
                    await CloseAgentWorkspaceTabAsync(
                        close.TabId,
                        cancellationToken),
                AgentWorkspaceLayoutRequest.PanelAdd add =>
                    await AddAgentWorkspacePanelAsync(
                        workspace,
                        add.TabId,
                        targetPanelId: null,
                        orientation: null,
                        add.Kind,
                        add.ConnectionRef,
                        cancellationToken),
                AgentWorkspaceLayoutRequest.PanelSplit split =>
                    await AddAgentWorkspacePanelAsync(
                        workspace,
                        tabId: null,
                        split.PanelId,
                        ToPresentationOrientation(split.Orientation),
                        split.Kind,
                        split.ConnectionRef,
                        cancellationToken),
                AgentWorkspaceLayoutRequest.PanelClose close =>
                    await CloseAgentWorkspacePanelAsync(
                        close.PanelId,
                        cancellationToken),
                AgentWorkspaceLayoutRequest.PanelConnect connect =>
                    await ConnectAgentWorkspacePanelAsync(
                        workspace,
                        connect,
                        cancellationToken),
                _ => false,
            };
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return await RecoverCommittedLayoutMutationAsync(
                request,
                before,
                workspace,
                expectedWorkspaceRevision);
        }

        if (!changed)
        {
            return await RecoverCommittedLayoutMutationAsync(
                request,
                before,
                workspace,
                expectedWorkspaceRevision);
        }

        try
        {
            var affectedPanel = FindAffectedAgentPanel(request, before, workspace);
            var isPanelReady = false;
            WorkspaceGraphSnapshot? snapshot;
            if (affectedPanel is not null
                && RequiresOperationalPanel(request, affectedPanel))
            {
                var readiness = await WaitForAgentPanelOperationalAsync(
                    workspace,
                    affectedPanel,
                    cancellationToken);
                if (readiness.StartupFailed)
                {
                    return new AgentWorkspaceLayoutMutationResult.Rejected(
                        "workspace_panel_startup_failed");
                }

                snapshot = readiness.Snapshot;
                isPanelReady = true;
            }
            else
            {
                snapshot = await ReadAgentWorkspaceSnapshotAsync(
                    workspace,
                    cancellationToken);
            }

            if (snapshot is null)
            {
                return await RecoverCommittedLayoutMutationAsync(
                    request,
                    before,
                    workspace,
                    expectedWorkspaceRevision);
            }

            return CreateAppliedLayoutMutation(
                request,
                before,
                snapshot,
                isPanelReady);
        }
        catch (Exception exception)
            when (exception is not OutOfMemoryException)
        {
            _ = exception;
            return await RecoverCommittedLayoutMutationAsync(
                request,
                before,
                workspace,
                expectedWorkspaceRevision);
        }
    }

    private async Task<AgentWorkspaceLayoutMutationResult>
        RecoverCommittedLayoutMutationAsync(
        AgentWorkspaceLayoutRequest request,
        WorkspaceInstance before,
        RuntimeWorkspaceViewModel workspace,
        long expectedWorkspaceRevision)
    {
        // The visual tree can commit before its graph registration receipt is
        // observed. Reconcile that post-commit region against the authoritative
        // host for a bounded interval instead of returning OutcomeUnknown for
        // a panel the user can already see.
        using var reconciliation = CancellationTokenSource.CreateLinkedTokenSource(
            _runtimeGraphLifetime.Token);
        reconciliation.CancelAfter(TimeSpan.FromSeconds(2));
        var delay = TimeSpan.FromMilliseconds(20);
        while (!reconciliation.IsCancellationRequested)
        {
            if (workspace.HostRevision > expectedWorkspaceRevision)
            {
                WorkspaceGraphSnapshot? snapshot = null;
                try
                {
                    snapshot = await ReadAgentWorkspaceSnapshotAsync(
                        workspace,
                        reconciliation.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                    when (exception is not OutOfMemoryException)
                {
                    _ = exception;
                }

                if (snapshot is not null
                    && !WorkspaceTopologyMatches(before, snapshot.Workspace))
                {
                    var recovered = CreateAppliedLayoutMutation(
                        request,
                        before,
                        snapshot,
                        isPanelReady: false);
                    if (recovered is AgentWorkspaceLayoutMutationResult.Applied)
                    {
                        return recovered;
                    }
                }
            }

            try
            {
                await Task.Delay(delay, reconciliation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromMilliseconds(Math.Min(
                delay.TotalMilliseconds * 2,
                250));
        }

        return new AgentWorkspaceLayoutMutationResult.OutcomeUnknown();
    }

    private static RuntimePanelViewModel? FindAffectedAgentPanel(
        AgentWorkspaceLayoutRequest request,
        WorkspaceInstance before,
        RuntimeWorkspaceViewModel workspace)
    {
        if (request is AgentWorkspaceLayoutRequest.PanelConnect connect)
        {
            return workspace.Tabs
                .SelectMany(tab => tab.Panels)
                .SingleOrDefault(panel => panel.Id == connect.PanelId);
        }

        if (request is not (
            AgentWorkspaceLayoutRequest.TabCreate
            or AgentWorkspaceLayoutRequest.PanelAdd
            or AgentWorkspaceLayoutRequest.PanelSplit))
        {
            return null;
        }

        var previousPanelIds = before.Tabs
            .SelectMany(tab => tab.Panels)
            .Select(panel => panel.Id)
            .ToHashSet();
        var created = workspace.Tabs
            .SelectMany(tab => tab.Panels)
            .Where(panel => !previousPanelIds.Contains(panel.Id))
            .ToArray();
        return created is [var panel] ? panel : null;
    }

    private static bool RequiresOperationalPanel(
        AgentWorkspaceLayoutRequest request,
        RuntimePanelViewModel panel)
    {
        if (panel is PanelPlaceholderViewModel)
        {
            return false;
        }

        // An unbound database panel is intentionally a connection chooser.
        // Once a connection_ref was supplied, or panel.connect was requested,
        // success means the resulting database session is actually linked.
        return panel.Kind != PanelKind.DatabaseViewer
            || request is AgentWorkspaceLayoutRequest.PanelConnect
            || request switch
            {
                AgentWorkspaceLayoutRequest.TabCreate create =>
                    create.ConnectionRef is not null,
                AgentWorkspaceLayoutRequest.PanelAdd add =>
                    add.ConnectionRef is not null,
                AgentWorkspaceLayoutRequest.PanelSplit split =>
                    split.ConnectionRef is not null,
                _ => false,
            };
    }

    /// <summary>
    /// A layout insertion is not a completed panel creation. The presentation
    /// initialization must finish and the authoritative workspace graph must
    /// link a live session to that exact panel before the agent can use it.
    /// </summary>
    private async Task<AgentPanelReadiness> WaitForAgentPanelOperationalAsync(
        RuntimeWorkspaceViewModel workspace,
        RuntimePanelViewModel panel,
        CancellationToken cancellationToken)
    {
        await AgentPanelInitialization(panel).WaitAsync(cancellationToken);
        if (AgentPanelInitializationFailed(panel))
        {
            return AgentPanelReadiness.Failed;
        }

        var delay = TimeSpan.FromMilliseconds(20);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await ReadAgentWorkspaceSnapshotAsync(
                workspace,
                cancellationToken,
                requirePanelSessionId: panel.Id);
            var state = await AgentPanelOperationalStateAsync(
                panel,
                cancellationToken);
            if (state == AgentPanelOperationalState.Failed)
            {
                return AgentPanelReadiness.Failed;
            }

            if (snapshot is not null && state == AgentPanelOperationalState.Ready)
            {
                return new AgentPanelReadiness(snapshot, StartupFailed: false);
            }

            await Task.Delay(delay, cancellationToken);
            delay = TimeSpan.FromMilliseconds(Math.Min(
                delay.TotalMilliseconds * 2,
                500));
        }
    }

    private static bool AgentPanelInitializationFailed(
        RuntimePanelViewModel panel) => panel switch
        {
            FileRuntimePanelViewModel files =>
                files.HostedClient is not { IsInitialized: true },
            BrowserRuntimePanelViewModel browser => browser.HasRouteError,
            TerminalRuntimePanelViewModel terminal => terminal.SessionRequest is null,
            StatisticsRuntimePanelViewModel statistics => !statistics.HasHostedSession,
            ProcessMonitorRuntimePanelViewModel processes => !processes.HasHostedSession,
            DatabaseRuntimePanelViewModel database => !database.HasHostedSession,
            RedisRuntimePanelViewModel redis => !redis.HasHostedSession,
            DockerRuntimePanelViewModel docker => !docker.HasHostedSession,
            GitRuntimePanelViewModel git => !git.HasHostedSession,
            _ => true,
        };

    private Task AgentPanelInitialization(RuntimePanelViewModel panel) => panel switch
    {
        FileRuntimePanelViewModel files => files.StartInitialization(),
        BrowserRuntimePanelViewModel browser =>
            browser.EnsureHostedRendererAsync(_runtimeGraphLifetime.Token),
        TerminalRuntimePanelViewModel terminal => terminal.Initialization,
        StatisticsRuntimePanelViewModel statistics => statistics.Start(),
        ProcessMonitorRuntimePanelViewModel processes => processes.Start(),
        DatabaseRuntimePanelViewModel database => database.StartHostingAsync(
            SessionClient,
            ClientId,
            FindAcceptedPanelOwner(database)
                ?? throw new InvalidOperationException(
                    "The accepted database panel has no workspace owner.")),
        RedisRuntimePanelViewModel redis => redis.StartHostingAsync(
            SessionClient,
            ClientId,
            FindAcceptedPanelOwner(redis)
                ?? throw new InvalidOperationException(
                    "The accepted Redis panel has no workspace owner.")),
        DockerRuntimePanelViewModel docker => docker.StartHostingAsync(
            SessionClient,
            ClientId,
            FindAcceptedPanelOwner(docker)
                ?? throw new InvalidOperationException(
                    "The accepted Docker panel has no workspace owner.")),
        GitRuntimePanelViewModel git => git.StartHostingAsync(
            SessionClient,
            ClientId,
            FindAcceptedPanelOwner(git)
                ?? throw new InvalidOperationException(
                    "The accepted Git panel has no workspace owner.")),
        _ => Task.CompletedTask,
    };

    private async Task<AgentPanelOperationalState> AgentPanelOperationalStateAsync(
        RuntimePanelViewModel panel,
        CancellationToken cancellationToken)
    {
        if (panel is BrowserRuntimePanelViewModel browser)
        {
            if (browser.RendererView is null || browser.HasRouteError)
            {
                return browser.HasRouteError
                    ? AgentPanelOperationalState.Failed
                    : AgentPanelOperationalState.Waiting;
            }

            var snapshot = await RuntimeGraph.EnsureBrowserSessionAsync(
                browser.SessionRequest,
                browser.SessionRequest.Owner,
                cancellationToken);
            if (snapshot is null)
            {
                return AgentPanelOperationalState.Failed;
            }

            var browserReady = snapshot.Descriptor.Health == SessionHealth.Healthy
            && browser.HasInteractiveAttachment;
            return browserReady
                ? AgentPanelOperationalState.Ready
                : AgentPanelOperationalState.Waiting;
        }

        var panelReady = panel switch
        {
            FileRuntimePanelViewModel files =>
                files.HostedClient is { IsInitialized: true },
            TerminalRuntimePanelViewModel terminal =>
                terminal.HasObservedActiveSession,
            StatisticsRuntimePanelViewModel statistics =>
                statistics.HasHostedSession,
            ProcessMonitorRuntimePanelViewModel processes =>
                processes.HasHostedSession,
            DatabaseRuntimePanelViewModel database => database.HasHostedSession,
            RedisRuntimePanelViewModel redis => redis.HasHostedSession,
            DockerRuntimePanelViewModel docker => docker.HasHostedSession,
            _ => false,
        };
        return panelReady
            ? AgentPanelOperationalState.Ready
            : AgentPanelOperationalState.Waiting;
    }

    private async Task<WorkspaceGraphSnapshot?> ReadAgentWorkspaceSnapshotAsync(
        RuntimeWorkspaceViewModel workspace,
        CancellationToken cancellationToken,
        PanelInstanceId? requirePanelSessionId = null)
    {
        var snapshot = await RuntimeGraph.ObserveWorkspaceAsync(
            workspace.Id,
            cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        if (requirePanelSessionId is { } panelId
            && snapshot.Workspace.Tabs
                .SelectMany(tab => tab.Panels)
                .SingleOrDefault(panel => panel.Id == panelId)
                ?.SessionId is null)
        {
            return null;
        }

        if (snapshot.Revision < workspace.HostRevision
            || snapshot.LastSequence < workspace.HostSequence
            || !WorkspaceTopologyMatches(
                CaptureRuntimeWorkspaceGraph(workspace),
                snapshot.Workspace))
        {
            // A user layout change can legitimately overtake the startup wait.
            // That invalidates this agent operation; it is not an invalid host
            // event and must not raise a global UI error toast.
            return null;
        }

        if (snapshot.LastSequence > workspace.HostSequence
            && !RuntimeGraph.TryApplyProjection(
                workspace,
                snapshot.WindowId,
                snapshot.Workspace,
                snapshot.Revision,
                snapshot.LastSequence,
                "agent panel readiness"))
        {
            return null;
        }

        return snapshot;
    }

    private enum AgentPanelOperationalState
    {
        Waiting,
        Ready,
        Failed,
    }

    private sealed record AgentPanelReadiness(
        WorkspaceGraphSnapshot? Snapshot,
        bool StartupFailed)
    {
        public static AgentPanelReadiness Failed { get; } = new(null, true);
    }

    private bool RequestKindIsSupported(AgentWorkspaceLayoutRequest request)
    {
        var kind = request switch
        {
            AgentWorkspaceLayoutRequest.TabCreate create => create.Kind,
            AgentWorkspaceLayoutRequest.PanelAdd add => add.Kind,
            AgentWorkspaceLayoutRequest.PanelSplit split => split.Kind,
            _ => (PanelKind?)null,
        };
        return kind is null
            || SupportedAgentWorkspacePanelKinds().Contains(kind.Value);
    }

    private bool RequestConnectionIsValid(
        AgentWorkspaceLayoutRequest request,
        RuntimeWorkspaceViewModel workspace)
    {
        if (!_agentWorkspaceLayoutPorts.TryGetValue(workspace.Id, out var port))
        {
            return request is not (
                AgentWorkspaceLayoutRequest.ConnectionList
                or AgentWorkspaceLayoutRequest.PanelConnect)
                && request is not AgentWorkspaceLayoutRequest.TabCreate
                { Kind: PanelKind.Terminal }
                && request is not AgentWorkspaceLayoutRequest.PanelAdd
                { Kind: PanelKind.Terminal }
                && request is not AgentWorkspaceLayoutRequest.PanelSplit
                { Kind: PanelKind.Terminal };
        }

        return request switch
        {
            AgentWorkspaceLayoutRequest.ConnectionList => true,
            AgentWorkspaceLayoutRequest.PanelConnect connect =>
                port.TryResolve(connect.ConnectionRef, out var target)
                && workspace.Tabs
                    .SelectMany(tab => tab.Panels)
                    .Single(panel => panel.Id == connect.PanelId) is { } panel
                && target.Supports(panel.Kind),
            AgentWorkspaceLayoutRequest.TabCreate create =>
                ValidCreationConnection(port, create.Kind, create.ConnectionRef),
            AgentWorkspaceLayoutRequest.PanelAdd add =>
                ValidCreationConnection(port, add.Kind, add.ConnectionRef),
            AgentWorkspaceLayoutRequest.PanelSplit split =>
                ValidCreationConnection(port, split.Kind, split.ConnectionRef),
            _ => true,
        };
    }

    private static bool ValidCreationConnection(
        MainWindowAgentWorkspaceLayoutPort port,
        PanelKind kind,
        string? connectionRef)
    {
        if (connectionRef is null)
        {
            return kind != PanelKind.Terminal;
        }

        return port.TryResolve(connectionRef, out var target)
            && target.Supports(kind)
            && (kind != PanelKind.Terminal
                || target.Selection is
                    PanelConnectionOptionViewModel.Target.Connection);
    }

    private static bool RequestTargetsCurrentWorkspace(
        AgentWorkspaceLayoutRequest request,
        RuntimeWorkspaceViewModel workspace) => request switch
        {
            AgentWorkspaceLayoutRequest.TabCreate => true,
            AgentWorkspaceLayoutRequest.ConnectionList => true,
            AgentWorkspaceLayoutRequest.PanelConnect connect =>
                workspace.Tabs.Any(tab =>
                    tab.Panels.Any(panel => panel.Id == connect.PanelId)),
            AgentWorkspaceLayoutRequest.TabClose close =>
                workspace.Tabs.Any(tab => tab.Id == close.TabId),
            AgentWorkspaceLayoutRequest.PanelAdd add =>
                workspace.Tabs.Any(tab => tab.Id == add.TabId),
            AgentWorkspaceLayoutRequest.PanelSplit split =>
                workspace.Tabs.Any(tab =>
                    tab.Panels.Any(panel => panel.Id == split.PanelId)),
            AgentWorkspaceLayoutRequest.PanelClose close =>
                workspace.Tabs.Any(tab =>
                    tab.Panels.Any(panel => panel.Id == close.PanelId)),
            _ => false,
        };

    private static bool RequestClosesUnsavedDatabaseChanges(
        AgentWorkspaceLayoutRequest request,
        RuntimeWorkspaceViewModel workspace)
    {
        IEnumerable<RuntimePanelViewModel> panels = request switch
        {
            AgentWorkspaceLayoutRequest.TabClose close =>
                workspace.Tabs
                    .Where(tab => tab.Id == close.TabId)
                    .SelectMany(tab => tab.Panels),
            AgentWorkspaceLayoutRequest.PanelClose close =>
                workspace.Tabs
                    .SelectMany(tab => tab.Panels)
                    .Where(panel => panel.Id == close.PanelId),
            AgentWorkspaceLayoutRequest.PanelConnect connect =>
                workspace.Tabs
                    .SelectMany(tab => tab.Panels)
                    .Where(panel => panel.Id == connect.PanelId),
            _ => [],
        };
        return panels
            .OfType<DatabaseRuntimePanelViewModel>()
            .Any(panel => panel.HasPendingChanges);
    }

    private Task<bool> CreateAgentWorkspaceTabAsync(
        RuntimeWorkspaceViewModel workspace,
        PanelKind kind,
        string? connectionRef,
        CancellationToken cancellationToken) =>
        AppendRuntimeTabAsync(
            workspace,
            runtime => CreateAgentWorkspaceTab(runtime, kind, connectionRef),
            "agent tab creation",
            cancellationToken,
            RuntimeGraphStaleProposalHandling.Reject);

    private RuntimeTabViewModel? CreateAgentWorkspaceTab(
        RuntimeWorkspaceViewModel workspace,
        PanelKind kind,
        string? connectionRef)
    {
        if (kind == PanelKind.Placeholder)
        {
            return CreateLauncherTab();
        }

        var title = kind == PanelKind.Terminal
            ? "Terminal"
            : SinglePanelTabTitle(kind);
        var source = kind is PanelKind.Statistics or PanelKind.ProcessMonitor
            ? "Local host"
            : "Local";
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), title, source);
        try
        {
            var panel = CreateAgentWorkspacePanel(
                workspace,
                tab,
                kind,
                connectionRef);
            if (panel is null)
            {
                return null;
            }

            AddPanelOrDispose(tab, panel);
            return tab;
        }
        catch
        {
            tab.DisposePanels();
            throw;
        }
    }

    private async Task<bool> AddAgentWorkspacePanelAsync(
        RuntimeWorkspaceViewModel workspace,
        TabInstanceId? tabId,
        PanelInstanceId? targetPanelId,
        PanelSplitOrientation? orientation,
        PanelKind kind,
        string? connectionRef,
        CancellationToken cancellationToken)
    {
        var tab = targetPanelId is { } target
            ? workspace.Tabs.SingleOrDefault(candidate =>
                candidate.Panels.Any(panel => panel.Id == target))
            : workspace.Tabs.SingleOrDefault(candidate => candidate.Id == tabId);
        if (tab is null)
        {
            return false;
        }

        var panel = CreateAgentWorkspacePanel(
            workspace,
            tab,
            kind,
            connectionRef);
        if (panel is null)
        {
            return false;
        }

        return await AddRuntimePanelUnderReceiptAsync(
            workspace,
            tab,
            panel,
            orientation is null ? "agent panel creation" : "agent panel split",
            () =>
            {
                var attached = orientation is { } split
                    ? tab.SplitWithPanel(targetPanelId!.Value, split, panel)
                    : AttachAgentWorkspacePanel(tab, panel);
                if (!attached)
                {
                    throw new InvalidOperationException(
                        "The host-approved panel could not be attached.");
                }

                if (panel is not PanelPlaceholderViewModel)
                {
                    StartTrackingRecovery(panel);
                }

                if (panel is TerminalRuntimePanelViewModel terminal)
                {
                    TrackRecentSession(terminal);
                }
            },
            cancellationToken,
            RuntimeGraphStaleProposalHandling.Reject);
    }

    private static bool AttachAgentWorkspacePanel(
        RuntimeTabViewModel tab,
        RuntimePanelViewModel panel)
    {
        tab.AddPanel(panel);
        return tab.ActivatePanel(panel.Id);
    }

    private RuntimePanelViewModel? CreateAgentWorkspacePanel(
        RuntimeWorkspaceViewModel workspace,
        RuntimeTabViewModel tab,
        PanelKind kind,
        string? connectionRef)
    {
        var id = PanelInstanceId.New();
        var target = ResolveAgentConnectionTarget(
            workspace.Id,
            connectionRef,
            kind);
        if (connectionRef is not null && target is null)
        {
            return null;
        }

        if (target?.Selection is
            PanelConnectionOptionViewModel.Target.Connection execution)
        {
            var connection = FindConnection(execution.Id);
            if (connection is null)
            {
                return null;
            }

            return kind switch
            {
                PanelKind.Terminal => CreateTerminalPanel(
                    workspace.Id,
                    tab.Id,
                    connection,
                    "Terminal",
                    PanelStartupBehavior.None),
                PanelKind.Browser => CreateBrowserPanel(
                    workspace.Id,
                    tab.Id,
                    id,
                    "Browser",
                    BrowserAddress.Blank,
                    connection),
                PanelKind.FileViewer => CreateFilePanel(
                    workspace.Id,
                    tab.Id,
                    id,
                    "File Viewer",
                    connection.Endpoint is ConnectionEndpoint.Ssh
                        ? ConnectionFileProviderProfiles.Id(connection.Id)
                        : BuiltInFileProviders.HomeId,
                    deferInitialization: true,
                    connection: connection),
                PanelKind.Statistics or PanelKind.ProcessMonitor =>
                    CreateMonitorPanel(
                        workspace.Id,
                        tab.Id,
                        id,
                        AgentWorkspacePanelTitle(kind),
                        kind,
                        connection),
                PanelKind.Docker => CreateDockerPanel(
                    id,
                    "Docker",
                    connection,
                    workspace.Id),
                _ => null,
            };
        }

        if (target?.Selection is
            PanelConnectionOptionViewModel.Target.FileProvider file)
        {
            return kind == PanelKind.FileViewer
                ? CreateFilePanel(
                    workspace.Id,
                    tab.Id,
                    id,
                    "File Viewer",
                    file.Id,
                    deferInitialization: true)
                : null;
        }

        if (target?.Selection is
            PanelConnectionOptionViewModel.Target.Database database)
        {
            var profile = FindDatabaseConnection(database.Id);
            return kind == PanelKind.DatabaseViewer && profile is not null
                ? CreateDatabasePanel(
                    id,
                    "Database",
                    tunnelConnection: ResolveDatabaseTunnel(profile),
                    savedConnection: profile,
                    workspaceId: workspace.Id)
                : null;
        }

        return kind switch
        {
            PanelKind.Placeholder => RuntimeTabViewModel.NewPlaceholder(),
            PanelKind.Terminal => null,
            PanelKind.Browser => CreateBrowserPanel(
                workspace.Id,
                tab.Id,
                id,
                "Browser",
                BrowserAddress.Blank),
            PanelKind.FileViewer => CreateFilePanel(
                workspace.Id,
                tab.Id,
                id,
                "File Viewer",
                deferInitialization: true),
            PanelKind.Statistics or PanelKind.ProcessMonitor =>
                CreateMonitorPanel(
                    workspace.Id,
                    tab.Id,
                    id,
                    AgentWorkspacePanelTitle(kind),
                    kind),
            PanelKind.DatabaseViewer => CreateDatabasePanel(
                id,
                "Database",
                workspaceId: workspace.Id),
            PanelKind.Docker => CreateDockerPanel(
                id,
                "Docker",
                workspaceId: workspace.Id),
            _ => null,
        };
    }

    private static string AgentWorkspacePanelTitle(PanelKind kind) => kind switch
    {
        PanelKind.Statistics => "Statistics",
        PanelKind.ProcessMonitor => "Process Monitor",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private AgentConnectionTarget? ResolveAgentConnectionTarget(
        WorkspaceInstanceId workspaceId,
        string? connectionRef,
        PanelKind kind)
    {
        if (connectionRef is null
            || !_agentWorkspaceLayoutPorts.TryGetValue(workspaceId, out var port)
            || !port.TryResolve(connectionRef, out var target)
            || !target.Supports(kind))
        {
            return null;
        }

        return target;
    }

    private async Task<bool> ConnectAgentWorkspacePanelAsync(
        RuntimeWorkspaceViewModel workspace,
        AgentWorkspaceLayoutRequest.PanelConnect request,
        CancellationToken cancellationToken)
    {
        if (!_agentWorkspaceLayoutPorts.TryGetValue(workspace.Id, out var port)
            || !port.TryResolve(request.ConnectionRef, out var target))
        {
            return false;
        }

        var panel = workspace.Tabs
            .SelectMany(tab => tab.Panels)
            .SingleOrDefault(candidate => candidate.Id == request.PanelId);
        if (panel is null || !target.Supports(panel.Kind))
        {
            return false;
        }

        var close = await ClosePanelAsync(
            panel.Id,
            CloseDecision.Confirm,
            cancellationToken);
        if (!CloseCompletedWithoutFailure(close))
        {
            return false;
        }

        return target.Selection switch
        {
            PanelConnectionOptionViewModel.Target.Connection connection
                when panel is TerminalRuntimePanelViewModel terminal =>
                ReplaceTerminalConnection(terminal, connection.Id),
            PanelConnectionOptionViewModel.Target.Connection connection =>
                FindConnection(connection.Id) is { } profile
                && ReplacePanelConnection(panel, profile),
            PanelConnectionOptionViewModel.Target.FileProvider file
                when panel is FileRuntimePanelViewModel filePanel =>
                ReplaceFilePanelProfile(filePanel, file.Id),
            PanelConnectionOptionViewModel.Target.Database database
                when panel.Kind == PanelKind.DatabaseViewer =>
                ReplaceDatabasePanelConnection(panel, database.Id),
            _ => false,
        };
    }

    private async Task<bool> CloseAgentWorkspaceTabAsync(
        TabInstanceId tabId,
        CancellationToken cancellationToken)
    {
        var close = await CloseTabAsync(
            tabId,
            CloseDecision.Confirm,
            cancellationToken);
        if (!CloseCompletedWithoutFailure(close))
        {
            return false;
        }

        return await RemoveTabAsync(
            tabId,
            cancellationToken,
            retryAfterGraphChange: false);
    }

    private async Task<bool> CloseAgentWorkspacePanelAsync(
        PanelInstanceId panelId,
        CancellationToken cancellationToken)
    {
        var close = await ClosePanelAsync(
            panelId,
            CloseDecision.Confirm,
            cancellationToken);
        if (!CloseCompletedWithoutFailure(close))
        {
            return false;
        }

        return await RemovePanelAsync(
            panelId,
            cancellationToken,
            retryAfterGraphChange: false);
    }

    private static bool CloseCompletedWithoutFailure(
        HostResult<CloseScopeResult> result) => result is
        HostResult<CloseScopeResult>.Success
        {
            Value: CloseScopeResult.Completed completed,
        }
        && completed.Sessions.All(session => session.Outcome is
            SessionCloseOutcome.GracefullyClosed
            or SessionCloseOutcome.ForceTerminated
            or SessionCloseOutcome.AlreadyClosed);

    private static PanelSplitOrientation ToPresentationOrientation(
        AgentPanelSplitOrientation orientation) => orientation switch
        {
            AgentPanelSplitOrientation.LeftRight => PanelSplitOrientation.LeftRight,
            AgentPanelSplitOrientation.TopBottom => PanelSplitOrientation.TopBottom,
            _ => throw new ArgumentOutOfRangeException(
                nameof(orientation),
                orientation,
                null),
        };

    private static AgentWorkspaceLayoutMutationResult CreateAppliedLayoutMutation(
        AgentWorkspaceLayoutRequest request,
        WorkspaceInstance before,
        WorkspaceGraphSnapshot after,
        bool isPanelReady = false)
    {
        var previousTabs = before.Tabs.Select(tab => tab.Id).ToHashSet();
        var previousPanels = before.Tabs
            .SelectMany(tab => tab.Panels)
            .Select(panel => panel.Id)
            .ToHashSet();
        return request switch
        {
            AgentWorkspaceLayoutRequest.TabCreate =>
                CreatedTab(after, previousTabs, isPanelReady),
            AgentWorkspaceLayoutRequest.TabClose close =>
                new AgentWorkspaceLayoutMutationResult.Applied(
                    after,
                    close.TabId,
                    null,
                    null),
            AgentWorkspaceLayoutRequest.PanelAdd add =>
                CreatedPanel(after, previousPanels, add.TabId, isPanelReady),
            AgentWorkspaceLayoutRequest.PanelSplit =>
                CreatedPanel(
                    after,
                    previousPanels,
                    tabId: null,
                    isPanelReady: isPanelReady),
            AgentWorkspaceLayoutRequest.PanelClose close =>
                new AgentWorkspaceLayoutMutationResult.Applied(
                    after,
                    FindTab(before, close.PanelId),
                    close.PanelId,
                    null),
            AgentWorkspaceLayoutRequest.PanelConnect connect =>
                new AgentWorkspaceLayoutMutationResult.Applied(
                    after,
                    FindTab(after.Workspace, connect.PanelId),
                    connect.PanelId,
                    after.Workspace.Tabs
                        .SelectMany(tab => tab.Panels)
                        .Single(panel => panel.Id == connect.PanelId)
                        .Kind,
                    isPanelReady),
            _ => new AgentWorkspaceLayoutMutationResult.OutcomeUnknown(),
        };
    }

    private static AgentWorkspaceLayoutMutationResult CreatedTab(
        WorkspaceGraphSnapshot after,
        IReadOnlySet<TabInstanceId> previousTabs,
        bool isPanelReady)
    {
        var created = after.Workspace.Tabs
            .Where(tab => !previousTabs.Contains(tab.Id))
            .ToArray();
        if (created is not [{ Panels: [var panel] } tab])
        {
            return new AgentWorkspaceLayoutMutationResult.OutcomeUnknown();
        }

        return new AgentWorkspaceLayoutMutationResult.Applied(
            after,
            tab.Id,
            panel.Id,
            panel.Kind,
            isPanelReady);
    }

    private static AgentWorkspaceLayoutMutationResult CreatedPanel(
        WorkspaceGraphSnapshot after,
        IReadOnlySet<PanelInstanceId> previousPanels,
        TabInstanceId? tabId,
        bool isPanelReady)
    {
        var created = after.Workspace.Tabs
            .SelectMany(tab => tab.Panels.Select(panel => (Tab: tab, Panel: panel)))
            .Where(item => !previousPanels.Contains(item.Panel.Id))
            .Where(item => tabId is null || item.Tab.Id == tabId)
            .ToArray();
        if (created is not [var item])
        {
            return new AgentWorkspaceLayoutMutationResult.OutcomeUnknown();
        }

        return new AgentWorkspaceLayoutMutationResult.Applied(
            after,
            item.Tab.Id,
            item.Panel.Id,
            item.Panel.Kind,
            isPanelReady);
    }

    private static TabInstanceId? FindTab(
        WorkspaceInstance workspace,
        PanelInstanceId panelId) =>
        workspace.Tabs
            .SingleOrDefault(tab =>
                tab.Panels.Any(panel => panel.Id == panelId))
            ?.Id;

    private static AgentWorkspaceLayoutMutationResult.Rejected
        RejectedLayoutMutation() =>
        new("workspace_layout_rejected");

    private sealed class MainWindowAgentWorkspaceLayoutPort(
        MainWindowViewModel owner,
        WorkspaceInstanceId workspaceId)
        : IAgentWorkspaceLayoutMutationPort
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, AgentConnectionTarget> _targets =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _referencesByIdentity =
            new(StringComparer.Ordinal);

        public WindowInstanceId WindowId => owner.WindowId;

        public WorkspaceInstanceId WorkspaceId { get; } = workspaceId;

        public IReadOnlySet<PanelKind> SupportedPanelKinds =>
            owner.SupportedAgentWorkspacePanelKinds();

        public async ValueTask<AgentWorkspaceLayoutMutationResult> MutateAsync(
            AgentWorkspaceLayoutRequest request,
            long expectedWorkspaceRevision,
            CancellationToken cancellationToken)
        {
            Task<AgentWorkspaceLayoutMutationResult>? mutation = null;
            await owner._uiThreadDispatcher.InvokeAsync(
                () => mutation = owner.MutateAgentWorkspaceLayoutAsync(
                        WorkspaceId,
                        request,
                        expectedWorkspaceRevision,
                        cancellationToken)
                    .AsTask(),
                cancellationToken);
            return await (mutation ?? throw new InvalidOperationException(
                "The UI dispatcher did not start the workspace layout mutation."));
        }

        public IReadOnlyList<AgentWorkspaceConnectionOption> ListConnections()
        {
            var candidates = BuildCandidates();
            lock (_gate)
            {
                var liveIdentities = candidates
                    .Select(candidate => candidate.Identity)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var stale in _referencesByIdentity.Keys
                    .Where(identity => !liveIdentities.Contains(identity))
                    .ToArray())
                {
                    if (_referencesByIdentity.Remove(stale, out var reference))
                    {
                        _targets.Remove(reference);
                    }
                }

                return [.. candidates.Select(candidate =>
                {
                    if (!_referencesByIdentity.TryGetValue(
                            candidate.Identity,
                            out var reference))
                    {
                        reference = $"connection_{Guid.NewGuid():N}";
                        _referencesByIdentity.Add(candidate.Identity, reference);
                    }

                    _targets[reference] = candidate;
                    return new AgentWorkspaceConnectionOption(
                        reference,
                        BoundedLabel(candidate.Name, 128),
                        BoundedLabel(candidate.Kind, 64),
                        candidate.SupportedKinds);
                })];
            }
        }

        public bool TryResolve(
            string reference,
            out AgentConnectionTarget target)
        {
            lock (_gate)
            {
                if (!_targets.TryGetValue(reference, out var resolved))
                {
                    target = null!;
                    return false;
                }

                var current = BuildCandidates().SingleOrDefault(candidate =>
                    string.Equals(
                        candidate.Identity,
                        resolved.Identity,
                        StringComparison.Ordinal));
                if (current is null)
                {
                    _targets.Remove(reference);
                    _referencesByIdentity.Remove(resolved.Identity);
                    target = null!;
                    return false;
                }

                _targets[reference] = current;
                target = current;
                return true;
            }
        }

        private AgentConnectionTarget[] BuildCandidates()
        {
            var execution = owner.PanelConnectionOptions
                .Where(option => option.CanOpen)
                .Select(option => new AgentConnectionTarget(
                    Identity(option.Selection),
                    option.Selection,
                    option.Name,
                    option.Kind,
                    ExecutionKinds(option.Selection)))
                .Where(candidate => candidate.SupportedKinds.Count > 0);
            var files = owner.FileConnectionOptions
                .Where(option => option.CanOpen)
                .Select(option => new AgentConnectionTarget(
                    Identity(option.Selection),
                    option.Selection,
                    option.Name,
                    option.Kind,
                    [PanelKind.FileViewer]));
            var databases = owner.DatabasePanelConnectionOptions
                .Where(option => option.CanOpen)
                .Select(option => new AgentConnectionTarget(
                    Identity(option.Selection),
                    option.Selection,
                    option.Name,
                    option.Kind,
                    [PanelKind.DatabaseViewer]));
            return [.. execution
                .Concat(files)
                .Concat(databases)
                .GroupBy(candidate => candidate.Identity, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Kind, StringComparer.OrdinalIgnoreCase)
                .Take(64)];
        }

        private static string BoundedLabel(string value, int maximumRunes)
        {
            var builder = new StringBuilder();
            foreach (var rune in value.EnumerateRunes().Take(maximumRunes))
            {
                builder.Append(rune);
            }

            return builder.Length == 0 ? "Unnamed" : builder.ToString();
        }

        private IReadOnlyList<PanelKind> ExecutionKinds(
            PanelConnectionOptionViewModel.Target selection)
        {
            if (selection is not PanelConnectionOptionViewModel.Target.Connection connection
                || owner.FindConnection(connection.Id)?.Endpoint is not
                    (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
            {
                return [];
            }

            return
            [
                PanelKind.Terminal,
                PanelKind.Browser,
                PanelKind.FileViewer,
                PanelKind.Statistics,
                PanelKind.ProcessMonitor,
                PanelKind.Docker,
            ];
        }

        private string Identity(
            PanelConnectionOptionViewModel.Target selection) => selection switch
            {
                PanelConnectionOptionViewModel.Target.Connection connection =>
                    $"execution:{connection.Id.Value}:{owner._catalog.Snapshot.Connections
                        .Single(item => item.Value.Id == connection.Id).Revision}",
                PanelConnectionOptionViewModel.Target.FileProvider file =>
                    $"file:{file.Id.Value}:{owner._catalog.Snapshot.FileProviderProfiles
                        .SingleOrDefault(item => item.Value.Id == file.Id)?.Revision ?? 0}",
                PanelConnectionOptionViewModel.Target.Database database =>
                    $"database:{database.Id.Value}:{owner._catalog.Snapshot.DatabaseConnections
                        .Single(item => item.Value.Id == database.Id).Revision}",
                _ => throw new ArgumentOutOfRangeException(nameof(selection)),
            };
    }

    private sealed record AgentConnectionTarget(
        string Identity,
        PanelConnectionOptionViewModel.Target Selection,
        string Name,
        string Kind,
        IReadOnlyList<PanelKind> SupportedKinds)
    {
        public bool Supports(PanelKind kind) => SupportedKinds.Contains(kind);
    }
}
