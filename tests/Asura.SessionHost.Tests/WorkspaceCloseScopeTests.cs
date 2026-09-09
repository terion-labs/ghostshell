using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

/// <summary>
/// Closing one workspace out of several.
///
/// There was no word for it. A window holds several workspaces, and the widest
/// scope narrower than the window was the tab — so anything that wanted to end
/// "this workspace" reached for Window and took every other workspace's
/// terminals down with it. These pin the scope that was missing.
/// </summary>
public sealed class WorkspaceCloseScopeTests
{
    private static readonly WorkspaceInstanceId Other = new("workspace-2");
    private static readonly SessionId OtherSession = new("session-2");

    [Fact]
    public async Task Closing_a_workspace_ends_its_sessions_and_leaves_the_others_running()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        await harness.OpenAsync(
            sessionId: OtherSession,
            panelId: new PanelInstanceId("panel-2"),
            workspaceId: Other,
            tabId: new TabInstanceId("tab-2"));

        var closed = (await harness.Client.CloseAsync(
            CloseScopeRequest.Workspace(harness.WorkspaceId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        var completed = Assert.IsType<CloseScopeResult.Completed>(closed);
        Assert.Equal(harness.SessionId, Assert.Single(completed.Sessions).SessionId);
        Assert.True(harness.Factory[harness.SessionId].IsClosed);
        Assert.False(harness.Factory[OtherSession].IsClosed);
    }

    /// <summary>
    /// The same confirmation a busy tab gets. Ending a workspace ends live
    /// sessions, and the number of them is the only difference.
    /// </summary>
    [Fact]
    public async Task A_workspace_with_busy_sessions_asks_before_it_ends_them()
    {
        await using var harness = new SessionHostTestHarness();
        harness.Factory.NewSessionsHaveActiveWork = true;
        await harness.OpenAsync();

        var requested = (await harness.Client.CloseAsync(
            CloseScopeRequest.Workspace(harness.WorkspaceId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        var confirmation = Assert.IsType<CloseScopeResult.ConfirmationRequired>(requested);
        Assert.Equal(CloseScopeKind.Workspace, confirmation.Scope);
        Assert.Single(confirmation.Sessions);
        Assert.False(harness.Factory[harness.SessionId].IsClosed);
    }

    /// <summary>
    /// The graph goes with the sessions — but only that workspace's. A window
    /// close still takes them all; this must not.
    /// </summary>
    [Fact]
    public async Task Closing_a_workspace_forgets_its_graph_and_keeps_the_window_s_others()
    {
        await using var harness = new SessionHostTestHarness();
        _ = (await harness.Client.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(harness.WindowId, Graph(harness.WorkspaceId)),
            harness.HumanContext(),
            CancellationToken.None)).Value();
        _ = (await harness.Client.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(harness.WindowId, Graph(Other)),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        _ = (await harness.Client.CloseAsync(
            CloseScopeRequest.Workspace(harness.WorkspaceId, CloseDecision.Request),
            harness.HumanContext(),
            CancellationToken.None)).Value();

        Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Failure>(
            await harness.Client.GetWorkspaceGraphAsync(
                harness.WorkspaceId,
                harness.HumanContext(),
                CancellationToken.None));
        Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Success>(
            await harness.Client.GetWorkspaceGraphAsync(
                Other,
                harness.HumanContext(),
                CancellationToken.None));
    }

    private static WorkspaceInstance Graph(WorkspaceInstanceId id)
    {
        var tab = new TabInstance(
            new TabInstanceId($"{id.Value}-tab"),
            "Tab",
            [
                new PanelInstance(
                    new PanelInstanceId($"{id.Value}-panel"),
                    PanelKind.Terminal,
                    "Terminal"),
            ],
            new PanelInstanceId($"{id.Value}-panel"));
        return new WorkspaceInstance(id, "Workspace", [tab], tab.Id);
    }
}
