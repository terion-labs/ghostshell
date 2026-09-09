using Asura.Application;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class WorkspaceGraphContractsTests
{
    [Fact]
    public void Registration_snapshot_and_event_copy_runtime_projections()
    {
        var workspace = Workspace();
        var request = new RegisterWorkspaceGraphRequest(
            new WindowInstanceId("window-1"),
            workspace);
        var snapshot = new WorkspaceGraphSnapshot(
            request.WindowId,
            request.Workspace,
            1,
            1);
        var workspaceEvent = new WorkspaceGraphEvent(
            request.WindowId,
            snapshot.Workspace,
            1,
            1,
            WorkspaceGraphEventKind.Registered,
            DateTimeOffset.UnixEpoch);

        Assert.NotSame(workspace, request.Workspace);
        Assert.NotSame(request.Workspace, snapshot.Workspace);
        Assert.NotSame(snapshot.Workspace, workspaceEvent.Workspace);
        Assert.NotSame(workspace.Tabs[0], request.Workspace.Tabs[0]);
        Assert.NotSame(request.Workspace.Tabs[0], snapshot.Workspace.Tabs[0]);
        Assert.Equal("workspace-1", workspaceEvent.WorkspaceId.Value);
        Assert.Equal(1, workspaceEvent.PayloadVersion);
    }

    [Fact]
    public void Session_link_events_identify_the_affected_runtime_entities()
    {
        var workspace = Workspace();
        var tab = workspace.Tabs[0];
        var panel = tab.Panels[0];
        var sessionId = new SessionId("session-1");
        var linked = workspace.ReplacePanelSession(tab.Id, panel.Id, sessionId);

        var workspaceEvent = new WorkspaceGraphEvent(
            new WindowInstanceId("window-1"),
            linked,
            2,
            2,
            WorkspaceGraphEventKind.PanelSessionLinked,
            DateTimeOffset.UnixEpoch,
            tab.Id,
            panel.Id,
            sessionId);

        Assert.Equal(tab.Id, workspaceEvent.TabId);
        Assert.Equal(panel.Id, workspaceEvent.PanelId);
        Assert.Equal(sessionId, workspaceEvent.SessionId);
        Assert.Equal(sessionId, workspaceEvent.Workspace.Tabs[0].Panels[0].SessionId);
        Assert.Throws<ArgumentException>(() => new WorkspaceGraphEvent(
            new WindowInstanceId("window-1"),
            linked,
            3,
            3,
            WorkspaceGraphEventKind.PanelSessionUnlinked,
            DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Graph_requests_reject_default_ids_and_invalid_cursors()
    {
        Assert.Throws<ArgumentException>(() => new RegisterWorkspaceGraphRequest(
            default,
            Workspace()));
        Assert.Throws<ArgumentException>(() => new UnregisterWorkspaceGraphRequest(
            new WindowInstanceId("window-1"),
            default));
        Assert.Throws<ArgumentException>(() => new ActivateWorkspaceTabRequest(
            default,
            new TabInstanceId("tab-1")));
        Assert.Throws<ArgumentException>(() => new ActivateWorkspacePanelRequest(
            new WorkspaceInstanceId("workspace-1"),
            default,
            new PanelInstanceId("panel-1")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WatchWorkspaceGraphRequest(
            new WorkspaceInstanceId("workspace-1"),
            -1));
    }

    private static WorkspaceInstance Workspace()
    {
        var panel = new PanelInstance(
            new PanelInstanceId("panel-1"),
            PanelKind.Terminal,
            "Terminal");
        var tab = new TabInstance(
            new TabInstanceId("tab-1"),
            "Tab",
            [panel],
            panel.Id);
        return new WorkspaceInstance(
            new WorkspaceInstanceId("workspace-1"),
            "Workspace",
            [tab],
            tab.Id);
    }
}
