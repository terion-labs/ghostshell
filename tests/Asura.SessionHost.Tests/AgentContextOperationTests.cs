using Asura.Application;
using Asura.Core;
using Asura.Protocol;
using static Asura.SessionHost.Tests.AgentContextTestSupport;

namespace Asura.SessionHost.Tests;

public sealed class AgentContextOperationTests
{
    [Fact]
    public async Task All_target_scopes_resolve_exact_graph_order_and_live_metadata()
    {
        await using var harness = new SessionHostTestHarness();
        var workspace = PrimaryWorkspace(harness);
        await RegisterAsync(harness, harness.WindowId, workspace);
        await harness.OpenAsync();

        var hello = (await harness.Client.NegotiateAsync(
            new ClientHello(
                [ProtocolVersions.Current],
                new CapabilitySet([SessionCapabilities.AgentContextInspect])),
            harness.HumanContext(),
            default)).Value();
        Assert.Contains(SessionCapabilities.AgentContextInspect, hello.Capabilities.Values, StringComparer.Ordinal);

        var exact = await InspectAsync(
            harness,
            new AgentTarget.Panel(
                harness.WindowId,
                harness.WorkspaceId,
                harness.TabId,
                harness.PanelId));
        var connection = await InspectAsync(
            harness,
            new AgentTarget.ConnectionSession(harness.SessionId));
        var tab = await InspectAsync(
            harness,
            new AgentTarget.OpenTab(
                harness.WindowId,
                harness.WorkspaceId,
                harness.TabId));
        var fullWorkspace = await InspectAsync(
            harness,
            new AgentTarget.Workspace(harness.WindowId, harness.WorkspaceId));
        var selected = await InspectAsync(
            harness,
            new AgentTarget.SelectedPanels(
            [
                new AgentTarget.Panel(
                    harness.WindowId,
                    harness.WorkspaceId,
                    new TabInstanceId("tab-secondary"),
                    new PanelInstanceId("panel-browser")),
                new AgentTarget.Panel(
                    harness.WindowId,
                    harness.WorkspaceId,
                    harness.TabId,
                    harness.PanelId),
            ]));

        Assert.Equal([harness.PanelId], PanelIds(exact));
        Assert.Equal([harness.PanelId], PanelIds(connection));
        Assert.Equal(
            [new PanelInstanceId("panel-files"), harness.PanelId],
            PanelIds(tab));
        Assert.Equal(
            [
                new PanelInstanceId("panel-files"),
                harness.PanelId,
                new PanelInstanceId("panel-browser"),
            ],
            PanelIds(fullWorkspace));
        Assert.Equal(
            [harness.PanelId, new PanelInstanceId("panel-browser")],
            PanelIds(selected));

        var terminal = Assert.Single(connection.Panels);
        Assert.Equal(harness.WindowId, terminal.WindowId);
        Assert.Equal(harness.WorkspaceId, terminal.WorkspaceId);
        Assert.Equal(harness.TabId, terminal.TabId);
        Assert.Equal(harness.SessionId, terminal.SessionId);
        Assert.Equal(SessionLifecycle.Starting, terminal.Lifecycle);
        Assert.True(terminal.HasRegisteredGraph);
        Assert.True(terminal.IsCurrentPanelSession);
        Assert.True(terminal.IsVisible);
        Assert.True(terminal.IsFocused);
        Assert.NotEmpty(terminal.Capabilities);
        Assert.Null(terminal.ConnectionId);
        Assert.Equal("Local terminal", terminal.ConnectionBoundary);
        Assert.Equal("/tmp", terminal.InitialWorkingDirectory);
        Assert.Equal("/tmp", terminal.CurrentWorkingDirectory);

        var files = fullWorkspace.Panels[0];
        var browser = fullWorkspace.Panels[2];
        Assert.True(files.IsVisible);
        Assert.False(files.IsFocused);
        Assert.False(browser.IsVisible);
        Assert.False(browser.IsFocused);

        Assert.Throws<NotSupportedException>(
            () => ((IList<AgentContextPanel>)fullWorkspace.Panels).Clear());
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)terminal.Capabilities).Clear());

        _ = (await harness.Client.ActivateWorkspacePanelAsync(
            new ActivateWorkspacePanelRequest(
                harness.WorkspaceId,
                harness.TabId,
                files.PanelId),
            harness.HumanContext(),
            default)).Value();
        Assert.True(terminal.IsFocused);
        Assert.False(files.IsFocused);

        var replacementId = new SessionId("replacement-session");
        await harness.OpenAsync(sessionId: replacementId);
        var superseded = await InspectAsync(
            harness,
            new AgentTarget.ConnectionSession(harness.SessionId));
        var currentPanel = await InspectAsync(
            harness,
            new AgentTarget.Panel(
                harness.WindowId,
                harness.WorkspaceId,
                harness.TabId,
                harness.PanelId));
        var exactOldSession = Assert.Single(superseded.Panels);
        Assert.Equal(harness.SessionId, exactOldSession.SessionId);
        Assert.False(exactOldSession.IsCurrentPanelSession);
        Assert.Equal(replacementId, Assert.Single(currentPanel.Panels).SessionId);
        Assert.Equal(
            exact.BindingFingerprint,
            AgentContextBindingFingerprint.Create(exact));
        Assert.NotEqual(exact.BindingFingerprint, currentPanel.BindingFingerprint);
    }

    [Fact]
    public async Task Workspace_scope_returns_every_panel_at_the_live_workspace_boundary()
    {
        await using var harness = new SessionHostTestHarness();
        var panels = Enumerable.Range(1, WorkspaceInstance.MaximumPanelCount)
            .Select(index => new PanelInstance(
                new PanelInstanceId($"panel-{index:D2}"),
                PanelKind.Browser,
                $"Browser {index:D2}"))
            .ToArray();
        var tab = new TabInstance(
            harness.TabId,
            "Complete workspace",
            panels,
            panels[0].Id);
        var workspace = new WorkspaceInstance(
            harness.WorkspaceId,
            "Workspace",
            [tab],
            tab.Id);
        await RegisterAsync(harness, harness.WindowId, workspace);

        var context = await InspectAsync(
            harness,
            new AgentTarget.Workspace(harness.WindowId, harness.WorkspaceId));

        Assert.Equal(WorkspaceInstance.MaximumPanelCount, context.Panels.Count);
        Assert.Equal(panels.Select(panel => panel.Id), PanelIds(context));
    }

    [Fact]
    public async Task Terminal_context_preserves_launch_boundary_and_refreshes_current_directory()
    {
        await using var harness = new SessionHostTestHarness();
        await RegisterAsync(harness, harness.WindowId, PrimaryWorkspace(harness));
        var connectionId = new ConnectionId("ssh-production");
        await harness.OpenAsync(
            launch: new TerminalLaunchRequest(
                null,
                "/usr/bin/ssh",
                ["production.example"],
                connectionId: connectionId,
                connectionMetadata: new TerminalConnectionMetadata(
                    "SSH: deploy@production.example:22",
                    "/srv/start")));

        var initial = Assert.Single((await InspectAsync(
            harness,
            new AgentTarget.ConnectionSession(harness.SessionId))).Panels);
        Assert.Equal(connectionId, initial.ConnectionId);
        Assert.Equal(
            "SSH: deploy@production.example:22",
            initial.ConnectionBoundary);
        Assert.Equal("/srv/start", initial.InitialWorkingDirectory);
        Assert.Equal("/srv/start", initial.CurrentWorkingDirectory);

        harness.Factory[harness.SessionId].ScreenWorkingDirectory = "/srv/current";
        var read = await harness.Client.ReadTerminalScreenAsync(
            harness.SessionId,
            harness.HumanContext(),
            CancellationToken.None);
        Assert.Equal("/srv/current", read.Value().WorkingDirectory);

        var refreshed = Assert.Single((await InspectAsync(
            harness,
            new AgentTarget.ConnectionSession(harness.SessionId))).Panels);
        Assert.Equal("/srv/start", refreshed.InitialWorkingDirectory);
        Assert.Equal("/srv/current", refreshed.CurrentWorkingDirectory);
        Assert.True(refreshed.SessionRevision > initial.SessionRevision);

        harness.Factory[harness.SessionId].ScreenWorkingDirectory =
            new string('x', TerminalConnectionMetadata.MaximumWorkingDirectoryBytes + 1);
        _ = (await harness.Client.ReadTerminalScreenAsync(
            harness.SessionId,
            harness.HumanContext(),
            CancellationToken.None)).Value();
        var afterOversizedRead = Assert.Single((await InspectAsync(
            harness,
            new AgentTarget.ConnectionSession(harness.SessionId))).Panels);
        Assert.Equal("/srv/current", afterOversizedRead.CurrentWorkingDirectory);
    }

    [Fact]
    public async Task Stale_targets_return_typed_not_found_failures()
    {
        await using var harness = new SessionHostTestHarness();
        await RegisterAsync(harness, harness.WindowId, PrimaryWorkspace(harness));

        var missingWorkspace = await InspectResultAsync(
            harness,
            new AgentTarget.Workspace(
                harness.WindowId,
                new WorkspaceInstanceId("missing-workspace")));
        var missingPanel = await InspectResultAsync(
            harness,
            new AgentTarget.Panel(
                harness.WindowId,
                harness.WorkspaceId,
                harness.TabId,
                new PanelInstanceId("missing-panel")));
        var missingTab = await InspectResultAsync(
            harness,
            new AgentTarget.OpenTab(
                harness.WindowId,
                harness.WorkspaceId,
                new TabInstanceId("missing-tab")));
        var panelFromAnotherTab = await InspectResultAsync(
            harness,
            new AgentTarget.Panel(
                harness.WindowId,
                harness.WorkspaceId,
                harness.TabId,
                new PanelInstanceId("panel-browser")));
        var wrongWindow = await InspectResultAsync(
            harness,
            new AgentTarget.Workspace(
                new WindowInstanceId("missing-window"),
                harness.WorkspaceId));
        var missingSession = await InspectResultAsync(
            harness,
            new AgentTarget.ConnectionSession(new SessionId("missing-session")));

        Assert.Equal(HostErrorCode.NotFound, missingWorkspace.Error().Code);
        Assert.Equal(HostErrorCode.NotFound, missingPanel.Error().Code);
        Assert.Equal(HostErrorCode.NotFound, missingTab.Error().Code);
        Assert.Equal(HostErrorCode.NotFound, panelFromAnotherTab.Error().Code);
        Assert.Equal(HostErrorCode.NotFound, wrongWindow.Error().Code);
        Assert.Equal(HostErrorCode.NotFound, missingSession.Error().Code);
    }

    [Fact]
    public async Task Exact_session_target_remains_exact_without_a_registered_graph()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();

        var context = await InspectAsync(
            harness,
            new AgentTarget.ConnectionSession(harness.SessionId));
        var panel = Assert.Single(context.Panels);

        Assert.Equal(harness.SessionId, panel.SessionId);
        Assert.Equal(harness.WindowId, panel.WindowId);
        Assert.Equal(harness.WorkspaceId, panel.WorkspaceId);
        Assert.False(panel.HasRegisteredGraph);
        Assert.False(panel.IsCurrentPanelSession);
        Assert.Null(panel.WorkspaceTitle);
        Assert.Null(panel.TabTitle);
        Assert.Null(panel.PanelTitle);
    }

    [Fact]
    public async Task Reused_raw_ids_never_widen_a_target_scope()
    {
        await using var harness = new SessionHostTestHarness();
        var workspaceA = CollidingWorkspace("workspace-a", "Only A", "only-a");
        var workspaceB = CollidingWorkspace("workspace-b", "Only B", "only-b");
        await RegisterAsync(
            harness,
            new WindowInstanceId("window-a"),
            workspaceA);
        await RegisterAsync(
            harness,
            new WindowInstanceId("window-b"),
            workspaceB);

        var exactA = await InspectAsync(
            harness,
            new AgentTarget.Panel(
                new WindowInstanceId("window-a"),
                workspaceA.Id,
                new TabInstanceId("shared-tab"),
                new PanelInstanceId("shared-panel")));
        var tabB = await InspectAsync(
            harness,
            new AgentTarget.OpenTab(
                new WindowInstanceId("window-b"),
                workspaceB.Id,
                new TabInstanceId("shared-tab")));
        var selected = await InspectAsync(
            harness,
            new AgentTarget.SelectedPanels(
            [
                new AgentTarget.Panel(
                    new WindowInstanceId("window-a"),
                    workspaceA.Id,
                    new TabInstanceId("shared-tab"),
                    new PanelInstanceId("shared-panel")),
                new AgentTarget.Panel(
                    new WindowInstanceId("window-a"),
                    workspaceA.Id,
                    new TabInstanceId("shared-tab"),
                    new PanelInstanceId("only-a")),
            ]));
        var crossWorkspacePanel = await InspectResultAsync(
            harness,
            new AgentTarget.Panel(
                new WindowInstanceId("window-a"),
                workspaceA.Id,
                new TabInstanceId("shared-tab"),
                new PanelInstanceId("only-b")));

        Assert.Equal("Workspace workspace-a", Assert.Single(exactA.Panels).WorkspaceTitle);
        Assert.Equal(
            [new PanelInstanceId("shared-panel"), new PanelInstanceId("only-b")],
            PanelIds(tabB));
        Assert.Equal(
            [new PanelInstanceId("only-a"), new PanelInstanceId("shared-panel")],
            PanelIds(selected));
        Assert.Equal(HostErrorCode.NotFound, crossWorkspacePanel.Error().Code);
    }

    [Fact]
    public async Task Bounds_cancellation_and_deadlines_return_typed_failures()
    {
        await using var harness = new SessionHostTestHarness();
        await RegisterAsync(harness, harness.WindowId, PrimaryWorkspace(harness));
        var oversizedWindow = new WindowInstanceId("oversized-window");
        var oversizedWorkspace = WorkspaceWithPanelId(
            "oversized-workspace",
            new string('p', 257));
        await RegisterAsync(harness, oversizedWindow, oversizedWorkspace);

        var bounded = await harness.Client.InspectAgentContextAsync(
            new AgentContextRequest(
                new AgentTarget.Workspace(harness.WindowId, harness.WorkspaceId),
                maximumPanelCount: 2),
            harness.HumanContext(),
            default);
        using var cancelledSource = new CancellationTokenSource();
        cancelledSource.Cancel();
        var cancelled = await harness.Client.InspectAgentContextAsync(
            new AgentContextRequest(
                new AgentTarget.Workspace(harness.WindowId, harness.WorkspaceId)),
            harness.HumanContext(),
            cancelledSource.Token);
        var expired = await harness.Client.InspectAgentContextAsync(
            new AgentContextRequest(
                new AgentTarget.Workspace(harness.WindowId, harness.WorkspaceId)),
            harness.HumanContext(deadline: harness.Clock.GetUtcNow()),
            default);
        var oversized = await InspectResultAsync(
            harness,
            new AgentTarget.Workspace(oversizedWindow, oversizedWorkspace.Id));

        Assert.Equal(HostErrorCode.InvalidRequest, bounded.Error().Code);
        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
        Assert.Equal(HostErrorCode.DeadlineExceeded, expired.Error().Code);
        Assert.Equal(HostErrorCode.InvalidRequest, oversized.Error().Code);
    }

}
