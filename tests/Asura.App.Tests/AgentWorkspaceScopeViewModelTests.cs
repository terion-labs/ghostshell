using Asura.App.ViewModels;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class AgentWorkspaceScopeViewModelTests
{
    [Fact]
    public void Workspace_and_current_tab_targets_use_the_attached_runtime_identity()
    {
        var windowId = WindowInstanceId.New();
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Workspace",
            "#112233",
            []);
        var tab = new RuntimeTabViewModel(
            TabInstanceId.New(),
            "Terminal tab",
            "test");
        workspace.Tabs.Add(tab);
        workspace.ActiveTab = tab;
        using var scope = Create(windowId);

        scope.AttachWorkspace(workspace);

        Assert.True(scope.TryCreateTarget(out var workspaceTarget, out var workspaceError));
        var exactWorkspace = Assert.IsType<AgentTarget.Workspace>(workspaceTarget);
        Assert.Equal(string.Empty, workspaceError);
        Assert.Equal(windowId, exactWorkspace.WindowId);
        Assert.Equal(workspace.Id, exactWorkspace.WorkspaceId);

        scope.SelectedScope = Assert.Single(
            scope.ScopeOptions,
            option => option.Kind == AgentRunScopeKind.CurrentTab);
        Assert.True(scope.TryCreateTarget(out var tabTarget, out var tabError));
        var exactTab = Assert.IsType<AgentTarget.OpenTab>(tabTarget);
        Assert.Equal(string.Empty, tabError);
        Assert.Equal(windowId, exactTab.WindowId);
        Assert.Equal(workspace.Id, exactTab.WorkspaceId);
        Assert.Equal(tab.Id, exactTab.TabId);
    }

    [Fact]
    public void Scope_change_is_rejected_while_the_host_reports_a_bound_run()
    {
        var canChange = true;
        using var scope = Create(
            WindowInstanceId.New(),
            canChangeScope: () => canChange);
        var original = scope.SelectedScope;
        var activePanel = Assert.Single(
            scope.ScopeOptions,
            option => option.Kind == AgentRunScopeKind.ActivePanel);

        canChange = false;
        scope.SelectedScope = activePanel;

        Assert.Same(original, scope.SelectedScope);
    }

    [Fact]
    public void Missing_workspace_and_disposed_owner_fail_at_the_owner_boundary()
    {
        var scope = Create(WindowInstanceId.New());

        Assert.False(scope.TryCreateTarget(out _, out var error));
        Assert.Contains("Open a terminal", error, StringComparison.Ordinal);

        scope.Dispose();

        Assert.Throws<ObjectDisposedException>(() => scope.TryCreateTarget(out _, out _));
        Assert.Throws<ObjectDisposedException>(() => scope.AttachWorkspace(null));
    }

    [Fact]
    public void Unknown_scope_option_is_rejected()
    {
        using var scope = Create(WindowInstanceId.New());
        var unknown = new AgentRunScopeOption((AgentRunScopeKind)int.MaxValue, "Unknown");

        Assert.Throws<ArgumentException>(() => scope.SelectedScope = unknown);
    }

    private static AgentWorkspaceScopeViewModel Create(
        WindowInstanceId windowId,
        Func<bool>? canChangeScope = null) =>
        new(
            windowId,
            canChangeScope ?? (() => true),
            () => true,
            _ => { });
}
