using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

public sealed class WorkspaceGraphOperationTests
{
    [Fact]
    public async Task ActivationTargetsIdsAndRejectsStaleRevisionsWithoutMutation()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = Workspace("workspace-1");
        var registeredResult = await RegisterAsync(harness, harness.WindowId, workspace);
        var registered = registeredResult.Value();
        Assert.Equal(1, registered.Revision);
        Assert.Equal(registered.Revision, ResultingRevision(registeredResult));

        var secondTab = workspace.Tabs[1];
        var tabActivated = (await harness.Client.ActivateWorkspaceTabAsync(
            new ActivateWorkspaceTabRequest(workspace.Id, secondTab.Id),
            harness.HumanContext(expectedRevision: registered.Revision),
            CancellationToken.None)).Value();
        Assert.Equal(2, tabActivated.Revision);
        Assert.Equal(secondTab.Id, tabActivated.Workspace.ActiveTabId);

        var firstTab = workspace.Tabs[0];
        var secondPanel = firstTab.Panels[1];
        var panelActivated = (await harness.Client.ActivateWorkspacePanelAsync(
            new ActivateWorkspacePanelRequest(workspace.Id, firstTab.Id, secondPanel.Id),
            harness.HumanContext(expectedRevision: tabActivated.Revision),
            CancellationToken.None)).Value();
        Assert.Equal(3, panelActivated.Revision);
        Assert.Equal(firstTab.Id, panelActivated.Workspace.ActiveTabId);
        Assert.Equal(secondPanel.Id, panelActivated.Workspace.Tabs[0].ActivePanelId);

        var stale = await harness.Client.ActivateWorkspaceTabAsync(
            new ActivateWorkspaceTabRequest(workspace.Id, secondTab.Id),
            harness.HumanContext(expectedRevision: tabActivated.Revision),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.RevisionConflict, stale.Error().Code);
        Assert.Equal(3, Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Failure>(stale).CurrentRevision);

        var unchanged = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(3, unchanged.Revision);
        Assert.Equal(firstTab.Id, unchanged.Workspace.ActiveTabId);
        Assert.Equal(secondPanel.Id, unchanged.Workspace.Tabs[0].ActivePanelId);
    }

    [Fact]
    public async Task UnknownWorkspaceTabAndPanelTargetsDoNotMutateTheGraph()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = Workspace("workspace-1");
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();

        var unknownWorkspace = await harness.Client.ActivateWorkspaceTabAsync(
            new ActivateWorkspaceTabRequest(
                new WorkspaceInstanceId("missing-workspace"),
                workspace.Tabs[0].Id),
            harness.HumanContext(),
            CancellationToken.None);
        var unknownTab = await harness.Client.ActivateWorkspaceTabAsync(
            new ActivateWorkspaceTabRequest(workspace.Id, new TabInstanceId("missing-tab")),
            harness.HumanContext(expectedRevision: registered.Revision),
            CancellationToken.None);
        var panelFromAnotherTab = await harness.Client.ActivateWorkspacePanelAsync(
            new ActivateWorkspacePanelRequest(
                workspace.Id,
                workspace.Tabs[0].Id,
                workspace.Tabs[1].Panels[0].Id),
            harness.HumanContext(expectedRevision: registered.Revision),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.NotFound, unknownWorkspace.Error().Code);
        Assert.Equal(HostErrorCode.NotFound, unknownTab.Error().Code);
        Assert.Equal(HostErrorCode.NotFound, panelFromAnotherTab.Error().Code);
        var snapshot = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(registered.Revision, snapshot.Revision);
        Assert.Equal(registered.LastSequence, snapshot.LastSequence);
    }

    [Fact]
    public async Task ReplacingSameWorkspaceIncrementsRevisionAndNoOpActivationDoesNot()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = Workspace("workspace-1");
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();
        var noOp = (await harness.Client.ActivateWorkspaceTabAsync(
            new ActivateWorkspaceTabRequest(workspace.Id, workspace.ActiveTabId),
            harness.HumanContext(expectedRevision: registered.Revision),
            CancellationToken.None)).Value();
        Assert.Equal(registered.Revision, noOp.Revision);
        Assert.Equal(registered.LastSequence, noOp.LastSequence);

        var replacementProjection = new WorkspaceInstance(
            workspace.Id,
            "Updated operations",
            workspace.Tabs,
            workspace.ActiveTabId);
        var replaced = (await RegisterAsync(
            harness,
            harness.WindowId,
            replacementProjection,
            expectedRevision: registered.Revision)).Value();

        Assert.Equal(registered.Revision + 1, replaced.Revision);
        Assert.Equal(registered.LastSequence + 1, replaced.LastSequence);
        Assert.Equal("Updated operations", replaced.Workspace.Title);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var events = harness.Client.WatchWorkspaceGraphAsync(
                new WatchWorkspaceGraphRequest(workspace.Id, registered.LastSequence),
                harness.HumanContext(),
                timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await events.MoveNextAsync());
        var replacementEvent = Assert.IsType<WorkspaceGraphStreamItem.Event>(events.Current).Value;
        Assert.Equal(WorkspaceGraphEventKind.Replaced, replacementEvent.Kind);
        Assert.Equal(replaced.Revision, replacementEvent.Revision);
    }

    [Fact]
    public async Task WatchReturnsOrderedEventsAndExplicitResynchronization()
    {
        await using var harness = new SessionHostTestHarness(eventRetention: 2);
        var workspace = Workspace("workspace-1");
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();
        var tabActivated = (await harness.Client.ActivateWorkspaceTabAsync(
            new ActivateWorkspaceTabRequest(workspace.Id, workspace.Tabs[1].Id),
            harness.HumanContext(expectedRevision: registered.Revision),
            CancellationToken.None)).Value();
        _ = (await harness.Client.ActivateWorkspacePanelAsync(
            new ActivateWorkspacePanelRequest(
                workspace.Id,
                workspace.Tabs[0].Id,
                workspace.Tabs[0].Panels[1].Id),
            harness.HumanContext(expectedRevision: tabActivated.Revision),
            CancellationToken.None)).Value();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var ordered = harness.Client.WatchWorkspaceGraphAsync(
                new WatchWorkspaceGraphRequest(workspace.Id, registered.LastSequence),
                harness.HumanContext(),
                timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await ordered.MoveNextAsync());
        var first = Assert.IsType<WorkspaceGraphStreamItem.Event>(ordered.Current).Value;
        Assert.True(await ordered.MoveNextAsync());
        var second = Assert.IsType<WorkspaceGraphStreamItem.Event>(ordered.Current).Value;
        Assert.Equal(WorkspaceGraphEventKind.TabActivated, first.Kind);
        Assert.Equal(WorkspaceGraphEventKind.PanelActivated, second.Kind);
        Assert.Equal(first.Sequence + 1, second.Sequence);
        Assert.Equal(first.Revision + 1, second.Revision);

        await using var stale = harness.Client.WatchWorkspaceGraphAsync(
                new WatchWorkspaceGraphRequest(workspace.Id, 0),
                harness.HumanContext(),
                timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await stale.MoveNextAsync());
        var resync = Assert.IsType<WorkspaceGraphStreamItem.ResynchronizationRequired>(stale.Current);
        Assert.Equal(3, resync.Snapshot.Revision);
        Assert.Equal(resync.Snapshot.LastSequence, resync.ResumeAfterSequence);
    }

    /// <summary>
    /// This used to assert the opposite — that a second workspace in a window
    /// evicted the first and completed its watchers. That was the bug written
    /// down as an invariant: it is why switching workspaces killed the sessions
    /// in the one you left. A window holds several workspaces; a watcher
    /// completes when its own workspace is removed, and only then.
    /// </summary>
    [Fact]
    public async Task RegisteringASecondWorkspaceLeavesTheFirstAndItsWatchersAlive()
    {
        await using var harness = new SessionHostTestHarness();
        var first = Workspace("workspace-old");
        var firstSnapshot = (await RegisterAsync(harness, harness.WindowId, first)).Value();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var watcher = harness.Client.WatchWorkspaceGraphAsync(
                new WatchWorkspaceGraphRequest(first.Id, firstSnapshot.LastSequence),
                harness.HumanContext(),
                timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        var pending = watcher.MoveNextAsync().AsTask();

        var second = Workspace("workspace-new");
        var registered = (await RegisterAsync(
            harness,
            harness.WindowId,
            second,
            expectedRevision: 0)).Value();

        // A workspace the registry has not seen starts at zero, whatever else
        // the window already holds.
        Assert.Equal(second.Id, registered.Workspace.Id);
        Assert.Equal(first.Id, (await harness.Client.GetWorkspaceGraphAsync(
            first.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value().Workspace.Id);
        Assert.False(pending.IsCompleted);

        // Removing it for real is what ends its watch.
        _ = (await harness.Client.UnregisterWorkspaceGraphAsync(
            new UnregisterWorkspaceGraphRequest(harness.WindowId, first.Id),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        Assert.True(await pending.WaitAsync(timeout.Token));
        var removed = Assert.IsType<WorkspaceGraphStreamItem.Event>(watcher.Current).Value;
        Assert.Equal(WorkspaceGraphEventKind.Removed, removed.Kind);
        Assert.False(await watcher.MoveNextAsync());
        Assert.Equal(HostErrorCode.NotFound, (await harness.Client.GetWorkspaceGraphAsync(
            first.Id,
            harness.HumanContext(),
            CancellationToken.None)).Error().Code);
        Assert.Equal(second.Id, (await harness.Client.GetWorkspaceGraphAsync(
            second.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value().Workspace.Id);
    }

    /// <summary>
    /// Re-registering a workspace already in the window is still a replacement,
    /// and still guarded by its revision.
    /// </summary>
    [Fact]
    public async Task ReRegisteringAWorkspaceStillHonoursItsRevision()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = Workspace("workspace-live");
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();

        var stale = await RegisterAsync(
            harness,
            harness.WindowId,
            Workspace("workspace-live", "Renamed"),
            expectedRevision: registered.Revision + 1);
        Assert.Equal(HostErrorCode.RevisionConflict, stale.Error().Code);

        var replaced = (await RegisterAsync(
            harness,
            harness.WindowId,
            Workspace("workspace-live", "Renamed"),
            expectedRevision: registered.Revision)).Value();
        Assert.Equal("Renamed", replaced.Workspace.Title);
    }

    [Fact]
    public async Task SuccessfulWindowCloseRemovesOwnedGraphAndSnapshotsDoNotAliasRegistryState()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = Workspace("workspace-1");
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();
        var queried = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.NotSame(workspace, registered.Workspace);
        Assert.NotSame(registered.Workspace, queried.Workspace);
        Assert.NotSame(registered.Workspace.Tabs[0], queried.Workspace.Tabs[0]);

        _ = (await harness.Client.CloseAsync(
            CloseScopeRequest.Window(harness.WindowId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        var removed = await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.NotFound, removed.Error().Code);
    }

    [Fact]
    public async Task UnregisterChecksRevisionAndTerminatesExistingWatchers()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = Workspace("workspace-1");
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var watcher = harness.Client.WatchWorkspaceGraphAsync(
                new WatchWorkspaceGraphRequest(workspace.Id, registered.LastSequence),
                harness.HumanContext(),
                timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        var pendingRemoval = watcher.MoveNextAsync().AsTask();

        var wrongWindow = await harness.Client.UnregisterWorkspaceGraphAsync(
            new UnregisterWorkspaceGraphRequest(
                new WindowInstanceId("window-2"),
                workspace.Id),
            harness.HumanContext(expectedRevision: registered.Revision),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.NotFound, wrongWindow.Error().Code);

        var stale = await harness.Client.UnregisterWorkspaceGraphAsync(
            new UnregisterWorkspaceGraphRequest(harness.WindowId, workspace.Id),
            harness.HumanContext(expectedRevision: registered.Revision - 1),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.RevisionConflict, stale.Error().Code);
        Assert.NotNull((await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value());

        var unregistered = await harness.Client.UnregisterWorkspaceGraphAsync(
            new UnregisterWorkspaceGraphRequest(harness.WindowId, workspace.Id),
            harness.HumanContext(expectedRevision: registered.Revision),
            CancellationToken.None);
        Assert.Equal(registered.Revision + 1, ResultingRevision(unregistered));
        Assert.True(await pendingRemoval.WaitAsync(timeout.Token));
        var removedEvent = Assert.IsType<WorkspaceGraphStreamItem.Event>(watcher.Current).Value;
        Assert.Equal(WorkspaceGraphEventKind.Removed, removedEvent.Kind);
        Assert.Equal(ResultingRevision(unregistered), removedEvent.Revision);
        Assert.False(await watcher.MoveNextAsync());

        var missing = await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.NotFound, missing.Error().Code);
        var repeated = await harness.Client.UnregisterWorkspaceGraphAsync(
            new UnregisterWorkspaceGraphRequest(harness.WindowId, workspace.Id),
            harness.HumanContext(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.NotFound, repeated.Error().Code);
    }

    [Fact]
    public async Task DisconnectingRegisteringClientRemovesItsWindowGraph()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = Workspace("workspace-1");
        _ = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();

        _ = (await harness.Client.DisconnectClientAsync(
            harness.ClientId,
            harness.HumanContext(),
            CancellationToken.None)).Value();

        var removed = await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.NotFound, removed.Error().Code);
    }

    [Fact]
    public async Task Ensuring_terminal_links_its_owned_panel_once_and_publishes_the_link()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.Terminal);
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();

        _ = await harness.OpenAsync();
        _ = await harness.OpenAsync();

        var linked = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(registered.Revision + 1, linked.Revision);
        Assert.Equal(registered.LastSequence + 1, linked.LastSequence);
        Assert.Equal(harness.SessionId, Assert.Single(Assert.Single(
            linked.Workspace.Tabs).Panels).SessionId);
        Assert.Equal(1, harness.Factory.CreateCount);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var watcher = harness.Client.WatchWorkspaceGraphAsync(
                new WatchWorkspaceGraphRequest(workspace.Id, registered.LastSequence),
                harness.HumanContext(),
                timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await watcher.MoveNextAsync());
        var workspaceEvent = Assert.IsType<WorkspaceGraphStreamItem.Event>(watcher.Current).Value;
        Assert.Equal(WorkspaceGraphEventKind.PanelSessionLinked, workspaceEvent.Kind);
        Assert.Equal(harness.TabId, workspaceEvent.TabId);
        Assert.Equal(harness.PanelId, workspaceEvent.PanelId);
        Assert.Equal(harness.SessionId, workspaceEvent.SessionId);
    }

    [Fact]
    public async Task Graphless_file_session_is_reconciled_when_its_workspace_registers()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.FileViewer);

        var ensured = await EnsureFileAsync(harness, harness.SessionId);
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();

        Assert.Equal(SessionLifecycle.Active, ensured.Descriptor.Lifecycle);
        Assert.Equal(1, registered.Revision);
        Assert.Equal(harness.SessionId, Assert.Single(Assert.Single(
            registered.Workspace.Tabs).Panels).SessionId);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var watcher = harness.Client.WatchWorkspaceGraphAsync(
                new WatchWorkspaceGraphRequest(workspace.Id, 0),
                harness.HumanContext(),
                timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await watcher.MoveNextAsync());
        var registeredEvent = Assert.IsType<WorkspaceGraphStreamItem.Event>(watcher.Current).Value;
        Assert.Equal(WorkspaceGraphEventKind.Registered, registeredEvent.Kind);
        Assert.Equal(harness.SessionId, Assert.Single(Assert.Single(
            registeredEvent.Workspace.Tabs).Panels).SessionId);
    }

    [Fact]
    public async Task Quick_terminal_session_does_not_conflict_with_a_registered_main_window_graph()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.Terminal);
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();
        var quickOwner = IndependentWindowOwner("quick-after-main");

        var ensured = await EnsureTerminalAsync(
            harness,
            new SessionId("quick-session-after-main"),
            quickOwner);

        Assert.Equal(SessionLifecycle.Starting, ensured.Descriptor.Lifecycle);
        var unchanged = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(registered.Revision, unchanged.Revision);
        Assert.Null(Assert.Single(Assert.Single(unchanged.Workspace.Tabs).Panels).SessionId);
    }

    [Fact]
    public async Task Existing_quick_terminal_session_is_ignored_when_main_window_graph_registers()
    {
        await using var harness = new SessionHostTestHarness();
        var quickOwner = IndependentWindowOwner("quick-before-main");
        _ = await EnsureTerminalAsync(
            harness,
            new SessionId("quick-session-before-main"),
            quickOwner);

        var registered = (await RegisterAsync(
            harness,
            harness.WindowId,
            OwnedWorkspace(harness, PanelKind.Terminal))).Value();

        Assert.Equal(1, registered.Revision);
        Assert.Null(Assert.Single(Assert.Single(
            registered.Workspace.Tabs).Panels).SessionId);
    }

    [Fact]
    public async Task Ensuring_and_closing_file_session_links_and_unlinks_its_owned_panel()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.FileViewer);
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();

        _ = await EnsureFileAsync(harness, harness.SessionId);
        var linked = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(registered.Revision + 1, linked.Revision);
        Assert.Equal(harness.SessionId, Assert.Single(Assert.Single(
            linked.Workspace.Tabs).Panels).SessionId);

        _ = (await harness.Client.CloseAsync(
            CloseScopeRequest.Session(harness.SessionId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var unlinked = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(linked.Revision + 1, unlinked.Revision);
        Assert.Null(Assert.Single(Assert.Single(unlinked.Workspace.Tabs).Panels).SessionId);
    }

    [Fact]
    public async Task Ensuring_a_closed_terminal_session_does_not_relink_its_panel()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.Terminal);
        _ = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();
        _ = await harness.OpenAsync();
        _ = (await harness.Client.CloseAsync(
            CloseScopeRequest.Session(harness.SessionId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var afterClose = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();

        var reopened = await harness.Client.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                harness.SessionId,
                Owner(harness),
                "Closed terminal",
                new TerminalLaunchRequest("/tmp")),
            harness.HumanContext(),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.SessionClosed, reopened.Error().Code);
        var unchanged = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(afterClose.Revision, unchanged.Revision);
        Assert.Null(Assert.Single(Assert.Single(unchanged.Workspace.Tabs).Panels).SessionId);
    }

    [Fact]
    public async Task Ensuring_a_closed_file_session_does_not_relink_its_panel()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.FileViewer);
        _ = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();
        _ = await EnsureFileAsync(harness, harness.SessionId);
        _ = (await harness.Client.CloseAsync(
            CloseScopeRequest.Session(harness.SessionId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var afterClose = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var location = new FilePanelLocation(
            "builtin.files.home",
            "local",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));

        var reopened = await harness.Client.EnsureFilePanelSessionAsync(
            new EnsureFilePanelSessionRequest(
                harness.SessionId,
                Owner(harness),
                "Closed files",
                location),
            harness.HumanContext(),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.SessionClosed, reopened.Error().Code);
        var unchanged = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(afterClose.Revision, unchanged.Revision);
        Assert.Null(Assert.Single(Assert.Single(unchanged.Workspace.Tabs).Panels).SessionId);
    }

    [Fact]
    public async Task Registration_discards_client_supplied_links_without_a_live_session()
    {
        await using var harness = new SessionHostTestHarness();
        var panel = new PanelInstance(
            harness.PanelId,
            PanelKind.Terminal,
            "Terminal",
            new SessionId("client-supplied-session"));
        var tab = new TabInstance(harness.TabId, "Owned tab", [panel], panel.Id);
        var proposal = new WorkspaceInstance(
            harness.WorkspaceId,
            "Owned workspace",
            [tab],
            tab.Id);

        var registered = (await RegisterAsync(
            harness,
            harness.WindowId,
            proposal)).Value();

        Assert.Null(Assert.Single(Assert.Single(
            registered.Workspace.Tabs).Panels).SessionId);
    }

    [Fact]
    public async Task Existing_graph_rejects_invalid_session_owners_without_creating_a_session()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.Terminal);
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();
        var owners = new[]
        {
            new SessionOwner(
                HostMode.Desktop,
                harness.WindowId,
                harness.WorkspaceId,
                new TabInstanceId("missing-tab"),
                harness.PanelId),
            new SessionOwner(
                HostMode.Desktop,
                harness.WindowId,
                harness.WorkspaceId,
                harness.TabId,
                new PanelInstanceId("missing-panel")),
            new SessionOwner(
                HostMode.Desktop,
                harness.WindowId,
                new WorkspaceInstanceId("missing-workspace"),
                harness.TabId,
                harness.PanelId),
            new SessionOwner(
                HostMode.Desktop,
                new WindowInstanceId("other-window"),
                harness.WorkspaceId,
                harness.TabId,
                harness.PanelId),
        };

        foreach (var (owner, index) in owners.Select((owner, index) => (owner, index)))
        {
            var result = await harness.Client.EnsureTerminalSessionAsync(
                new EnsureTerminalSessionRequest(
                    new SessionId($"invalid-session-{index}"),
                    owner,
                    "Invalid owner",
                    new TerminalLaunchRequest("/tmp")),
                harness.HumanContext(),
                CancellationToken.None);
            Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        }

        Assert.Equal(0, harness.Factory.CreateCount);
        var unchanged = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(registered.Revision, unchanged.Revision);
        Assert.Null(Assert.Single(Assert.Single(unchanged.Workspace.Tabs).Panels).SessionId);
    }

    [Fact]
    public async Task Existing_graph_rejects_a_session_kind_that_does_not_match_the_panel()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.FileViewer);
        var registered = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();

        var result = await harness.Client.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                harness.SessionId,
                Owner(harness),
                "Wrong kind",
                new TerminalLaunchRequest("/tmp")),
            harness.HumanContext(),
            CancellationToken.None);

        Assert.Equal(HostErrorCode.InvalidRequest, result.Error().Code);
        Assert.Equal(0, harness.Factory.CreateCount);
        var unchanged = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(registered.Revision, unchanged.Revision);
    }

    [Fact]
    public async Task Embedded_terminal_is_owned_by_a_docker_panel_without_claiming_its_primary_link()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.Docker);
        _ = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();

        var opened = await harness.Client.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                harness.SessionId,
                Owner(harness),
                "Container shell",
                new TerminalLaunchRequest("/tmp"),
                PanelSessionRole.Embedded),
            harness.HumanContext(),
            CancellationToken.None);

        Assert.Equal(harness.SessionId, opened.Value().Descriptor.Id);
        var afterOpen = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Null(Assert.Single(Assert.Single(afterOpen.Workspace.Tabs).Panels).SessionId);

        var replaced = await RegisterAsync(
            harness,
            harness.WindowId,
            workspace,
            afterOpen.Revision);
        Assert.Null(Assert.Single(Assert.Single(
            replaced.Value().Workspace.Tabs).Panels).SessionId);

        var closed = (await harness.Client.CloseAsync(
            CloseScopeRequest.Panel(harness.PanelId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var completed = Assert.IsType<CloseScopeResult.Completed>(closed);
        Assert.Equal(harness.SessionId, Assert.Single(completed.Sessions).SessionId);
    }

    [Fact]
    public async Task Registration_rejects_an_already_live_session_with_an_invalid_panel_reference()
    {
        await using var harness = new SessionHostTestHarness();
        var invalidOwner = new SessionOwner(
            HostMode.Desktop,
            harness.WindowId,
            harness.WorkspaceId,
            harness.TabId,
            new PanelInstanceId("orphan-panel"));
        var opened = await harness.Client.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                harness.SessionId,
                invalidOwner,
                "Graphless terminal",
                new TerminalLaunchRequest("/tmp")),
            harness.HumanContext(),
            CancellationToken.None);
        Assert.NotNull(opened.Value());

        var registration = await RegisterAsync(
            harness,
            harness.WindowId,
            OwnedWorkspace(harness, PanelKind.Terminal));

        Assert.Equal(HostErrorCode.InvalidRequest, registration.Error().Code);
        var missing = await harness.Client.GetWorkspaceGraphAsync(
            harness.WorkspaceId,
            harness.HumanContext(),
            CancellationToken.None);
        Assert.Equal(HostErrorCode.NotFound, missing.Error().Code);
    }

    [Fact]
    public async Task Structural_replacement_preserves_the_authoritative_reconnect_session()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.Terminal);
        _ = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();
        var replacementId = new SessionId("session-2");
        _ = await harness.OpenAsync();
        _ = await harness.OpenAsync(sessionId: replacementId);
        var linked = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();

        var replaced = (await RegisterAsync(
            harness,
            harness.WindowId,
            workspace,
            linked.Revision)).Value();

        Assert.Equal(linked.Revision + 1, replaced.Revision);
        Assert.Equal(replacementId, Assert.Single(Assert.Single(
            replaced.Workspace.Tabs).Panels).SessionId);
    }

    [Fact]
    public async Task Structural_replacement_does_not_resurrect_a_superseded_session()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.Terminal);
        _ = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();
        var replacementId = new SessionId("session-2");
        _ = await harness.OpenAsync();
        _ = await harness.OpenAsync(sessionId: replacementId);
        _ = (await harness.Client.CloseAsync(
            CloseScopeRequest.Session(replacementId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var unlinked = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Null(Assert.Single(Assert.Single(unlinked.Workspace.Tabs).Panels).SessionId);

        var replaced = (await RegisterAsync(
            harness,
            harness.WindowId,
            workspace,
            unlinked.Revision)).Value();

        Assert.Equal(unlinked.Revision + 1, replaced.Revision);
        Assert.Null(Assert.Single(Assert.Single(replaced.Workspace.Tabs).Panels).SessionId);
    }

    [Fact]
    public async Task Closing_an_old_session_cannot_unlink_its_replacement()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = OwnedWorkspace(harness, PanelKind.Terminal);
        _ = (await RegisterAsync(harness, harness.WindowId, workspace)).Value();
        var replacementId = new SessionId("session-2");
        _ = await harness.OpenAsync();
        _ = await harness.OpenAsync(sessionId: replacementId);
        var replaced = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(3, replaced.Revision);
        Assert.Equal(replacementId, Assert.Single(Assert.Single(
            replaced.Workspace.Tabs).Panels).SessionId);

        _ = (await harness.Client.CloseAsync(
            CloseScopeRequest.Session(harness.SessionId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var afterOldClose = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(replaced.Revision, afterOldClose.Revision);
        Assert.Equal(replacementId, Assert.Single(Assert.Single(
            afterOldClose.Workspace.Tabs).Panels).SessionId);

        _ = (await harness.Client.CloseAsync(
            CloseScopeRequest.Session(replacementId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var unlinked = (await harness.Client.GetWorkspaceGraphAsync(
            workspace.Id,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        Assert.Equal(replaced.Revision + 1, unlinked.Revision);
        Assert.Null(Assert.Single(Assert.Single(unlinked.Workspace.Tabs).Panels).SessionId);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var watcher = harness.Client.WatchWorkspaceGraphAsync(
                new WatchWorkspaceGraphRequest(workspace.Id, replaced.LastSequence),
                harness.HumanContext(),
                timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await watcher.MoveNextAsync());
        var workspaceEvent = Assert.IsType<WorkspaceGraphStreamItem.Event>(watcher.Current).Value;
        Assert.Equal(WorkspaceGraphEventKind.PanelSessionUnlinked, workspaceEvent.Kind);
        Assert.Equal(replacementId, workspaceEvent.SessionId);
    }

    [Fact]
    public async Task Disposal_waits_for_in_flight_session_creation_and_disposes_the_result()
    {
        await using var harness = new SessionHostTestHarness();
        harness.Factory.BlockCreation = true;
        var opening = harness.OpenAsync().AsTask();
        await harness.Factory.CreationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposal = harness.Client.DisposeAsync().AsTask();
        await Task.Yield();
        Assert.False(disposal.IsCompleted);

        harness.Factory.AllowCreation.TrySetResult();
        _ = await opening;
        await disposal;

        Assert.True(harness.Factory[harness.SessionId].IsClosed);
    }

    [Fact]
    public async Task Terminal_ensure_cancelled_while_waiting_for_graph_gate_returns_a_failure()
    {
        await using var harness = new SessionHostTestHarness();
        harness.Factory.BlockCreation = true;
        var opening = harness.OpenAsync().AsTask();
        await harness.Factory.CreationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var queued = harness.Client.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                new SessionId("queued-session"),
                Owner(harness),
                "Queued terminal",
                new TerminalLaunchRequest("/tmp")),
            harness.HumanContext(),
            cancellation.Token);

        cancellation.Cancel();
        var cancelled = await queued;
        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);

        harness.Factory.AllowCreation.TrySetResult();
        _ = await opening;
    }

    private static async ValueTask<HostResult<WorkspaceGraphSnapshot>> RegisterAsync(
        SessionHostTestHarness harness,
        WindowInstanceId windowId,
        WorkspaceInstance workspace,
        long? expectedRevision = null) =>
        await harness.Client.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(windowId, workspace),
            harness.HumanContext(expectedRevision: expectedRevision),
            CancellationToken.None);

    private static long ResultingRevision(HostResult<WorkspaceGraphSnapshot> result) =>
        Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Success>(result).ResultingRevision;

    private static long ResultingRevision(HostResult<Unit> result) =>
        Assert.IsType<HostResult<Unit>.Success>(result).ResultingRevision;

    private static SessionOwner Owner(SessionHostTestHarness harness) => new(
        HostMode.Desktop,
        harness.WindowId,
        harness.WorkspaceId,
        harness.TabId,
        harness.PanelId);

    private static WorkspaceInstance OwnedWorkspace(
        SessionHostTestHarness harness,
        PanelKind kind)
    {
        var panel = new PanelInstance(harness.PanelId, kind, kind.ToString());
        var tab = new TabInstance(harness.TabId, "Owned tab", [panel], panel.Id);
        return new WorkspaceInstance(
            harness.WorkspaceId,
            "Owned workspace",
            [tab],
            tab.Id);
    }

    private static async ValueTask<SessionSnapshot> EnsureFileAsync(
        SessionHostTestHarness harness,
        SessionId sessionId)
    {
        var location = new FilePanelLocation(
            "builtin.files.home",
            "local",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        return (await harness.Client.EnsureFilePanelSessionAsync(
            new EnsureFilePanelSessionRequest(
                sessionId,
                Owner(harness),
                "Files",
                location),
            harness.HumanContext(),
            CancellationToken.None)).Value();
    }

    private static async ValueTask<SessionSnapshot> EnsureTerminalAsync(
        SessionHostTestHarness harness,
        SessionId sessionId,
        SessionOwner owner) =>
        (await harness.Client.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                sessionId,
                owner,
                "Independent terminal",
                new TerminalLaunchRequest("/tmp")),
            harness.HumanContext(),
            CancellationToken.None)).Value();

    private static SessionOwner IndependentWindowOwner(string suffix) => new(
        HostMode.Desktop,
        new WindowInstanceId($"{suffix}-window"),
        new WorkspaceInstanceId($"{suffix}-workspace"),
        new TabInstanceId($"{suffix}-tab"),
        new PanelInstanceId($"{suffix}-panel"));

    private static WorkspaceInstance Workspace(string id, string title = "Operations")
    {
        var terminal = new PanelInstance(
            new PanelInstanceId($"{id}-terminal"),
            PanelKind.Terminal,
            "Terminal");
        var files = new PanelInstance(
            new PanelInstanceId($"{id}-files"),
            PanelKind.FileViewer,
            "Files");
        var browser = new PanelInstance(
            new PanelInstanceId($"{id}-browser"),
            PanelKind.Browser,
            "Browser");
        var primary = new TabInstance(
            new TabInstanceId($"{id}-primary"),
            "Primary",
            [terminal, files],
            terminal.Id);
        var secondary = new TabInstance(
            new TabInstanceId($"{id}-secondary"),
            "Secondary",
            [browser],
            browser.Id);
        return new WorkspaceInstance(
            new WorkspaceInstanceId(id),
            title,
            [primary, secondary],
            primary.Id);
    }
}
