using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

/// <summary>
/// A window shows one workspace at a time and holds several.
///
/// The host used to hold one per window and evict the previous on
/// registration — it removed the graph, the client saw the removal and closed
/// what it believed had ended, and every session in the workspace you had just
/// switched away from died. Switching is changing view, so these pin the host
/// side of that.
/// </summary>
public sealed class WorkspaceGraphWindowOwnershipTests
{
    private static readonly WindowInstanceId Window = new("ownership-window");

    [Fact]
    public async Task A_second_workspace_in_a_window_does_not_evict_the_first()
    {
        await using var host = CreateHost();
        var first = Workspace("first");
        var second = Workspace("second");

        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(Window, first),
            Context(),
            default)).Value();
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(Window, second),
            Context(),
            default)).Value();

        // Both still resolve: the first was not removed to make room.
        var firstGraph = await host.GetWorkspaceGraphAsync(first.Id, Context(), default);
        var secondGraph = await host.GetWorkspaceGraphAsync(second.Id, Context(), default);
        Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Success>(firstGraph);
        Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Success>(secondGraph);
        Assert.Equal(first.Id, firstGraph.Value().Workspace.Id);
        Assert.Equal(second.Id, secondGraph.Value().Workspace.Id);
    }

    /// <summary>
    /// Re-registering a workspace already in the window is still a replacement
    /// of that one — the case a live workspace's own graph updates through.
    /// </summary>
    [Fact]
    public async Task Re_registering_the_same_workspace_replaces_only_itself()
    {
        await using var host = CreateHost();
        var first = Workspace("first");
        var second = Workspace("second");
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(Window, first),
            Context(),
            default)).Value();
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(Window, second),
            Context(),
            default)).Value();

        var renamed = Workspace("first", "Renamed");
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(Window, renamed),
            Context(),
            default)).Value();

        var firstGraph = await host.GetWorkspaceGraphAsync(first.Id, Context(), default);
        Assert.Equal("Renamed", firstGraph.Value().Workspace.Title);
        Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Success>(
            await host.GetWorkspaceGraphAsync(second.Id, Context(), default));
    }

    [Fact]
    public async Task Unregistering_one_workspace_leaves_the_window_others()
    {
        await using var host = CreateHost();
        var first = Workspace("first");
        var second = Workspace("second");
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(Window, first),
            Context(),
            default)).Value();
        _ = (await host.RegisterWorkspaceGraphAsync(
            new RegisterWorkspaceGraphRequest(Window, second),
            Context(),
            default)).Value();

        _ = (await host.UnregisterWorkspaceGraphAsync(
            new UnregisterWorkspaceGraphRequest(Window, first.Id),
            Context(),
            default)).Value();

        Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Failure>(
            await host.GetWorkspaceGraphAsync(first.Id, Context(), default));
        Assert.IsType<HostResult<WorkspaceGraphSnapshot>.Success>(
            await host.GetWorkspaceGraphAsync(second.Id, Context(), default));
    }

    private static InMemorySessionHostClient CreateHost() =>
        new(
            new FakeTerminalSessionFactory(),
            new DesktopLifecyclePolicy(),
            TimeProvider.System);

    private static OperationContext Context() =>
        OperationContext.ForHuman(new ClientId("ownership-client"));

    private static WorkspaceInstance Workspace(string id, string title = "Workspace")
    {
        var tab = new TabInstance(
            new TabInstanceId($"{id}-tab"),
            "Tab",
            [
                new PanelInstance(
                    new PanelInstanceId($"{id}-panel"),
                    PanelKind.Terminal,
                    "Terminal"),
            ],
            new PanelInstanceId($"{id}-panel"));
        return new WorkspaceInstance(
            new WorkspaceInstanceId(id),
            title,
            [tab],
            tab.Id);
    }
}
