using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

internal static class AgentContextTestSupport
{
    public static async ValueTask RegisterAsync(
        SessionHostTestHarness harness,
        WindowInstanceId windowId,
        WorkspaceInstance workspace)
    {
        _ = (await harness.Client.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(windowId, workspace),
            harness.HumanContext(),
            default)).Value();
    }

    public static async ValueTask<AgentContextSnapshot> InspectAsync(
        SessionHostTestHarness harness,
        AgentTarget target) =>
        (await InspectResultAsync(harness, target)).Value();

    public static async ValueTask<HostResult<AgentContextSnapshot>> InspectResultAsync(
        SessionHostTestHarness harness,
        AgentTarget target) =>
        await harness.Client.InspectAgentContextAsync(
            new AgentContextRequest(target),
            SessionHostTestHarness.AgentContext(),
            default);

    public static PanelInstanceId[] PanelIds(AgentContextSnapshot context) =>
        [.. context.Panels.Select(panel => panel.PanelId)];

    public static WorkspaceInstance PrimaryWorkspace(SessionHostTestHarness harness)
    {
        var files = new PanelInstance(
            new PanelInstanceId("panel-files"),
            PanelKind.FileViewer,
            "Files");
        var terminal = new PanelInstance(
            harness.PanelId,
            PanelKind.Terminal,
            "Terminal");
        var browser = new PanelInstance(
            new PanelInstanceId("panel-browser"),
            PanelKind.Browser,
            "Browser");
        var primary = new TabInstance(
            harness.TabId,
            "Primary",
            [files, terminal],
            terminal.Id);
        var secondary = new TabInstance(
            new TabInstanceId("tab-secondary"),
            "Secondary",
            [browser],
            browser.Id);
        return new WorkspaceInstance(
            harness.WorkspaceId,
            "Workspace",
            [primary, secondary],
            primary.Id);
    }

    public static WorkspaceInstance CollidingWorkspace(
        string workspaceId,
        string uniqueTitle,
        string uniquePanelId)
    {
        var shared = new PanelInstance(
            new PanelInstanceId("shared-panel"),
            PanelKind.Browser,
            $"Shared {workspaceId}");
        var unique = new PanelInstance(
            new PanelInstanceId(uniquePanelId),
            PanelKind.FileViewer,
            uniqueTitle);
        var tab = new TabInstance(
            new TabInstanceId("shared-tab"),
            "Shared tab",
            [shared, unique],
            shared.Id);
        return new WorkspaceInstance(
            new WorkspaceInstanceId(workspaceId),
            $"Workspace {workspaceId}",
            [tab],
            tab.Id);
    }

    public static WorkspaceInstance WorkspaceWithPanelId(
        string workspaceId,
        string panelId)
    {
        var panel = new PanelInstance(
            new PanelInstanceId(panelId),
            PanelKind.Browser,
            "Browser");
        var tab = new TabInstance(
            new TabInstanceId("bounded-tab"),
            "Bounded tab",
            [panel],
            panel.Id);
        return new WorkspaceInstance(
            new WorkspaceInstanceId(workspaceId),
            "Bounded workspace",
            [tab],
            tab.Id);
    }
}
