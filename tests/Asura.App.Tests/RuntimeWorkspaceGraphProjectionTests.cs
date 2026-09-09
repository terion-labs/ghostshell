using Asura.App.ViewModels;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class RuntimeWorkspaceGraphProjectionTests
{
    [Fact]
    public void Capture_preserves_placeholder_and_exact_active_identities()
    {
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Runtime",
            "#123456",
            []);
        var first = CreateTab("First");
        var second = CreateTab("Second");
        workspace.Tabs.Add(first);
        workspace.Tabs.Add(second);
        workspace.ActiveTab = second;

        var graph = RuntimeWorkspaceGraphProjection.Capture(workspace);

        Assert.Equal(workspace.Id, graph.Id);
        Assert.Equal(second.Id, graph.ActiveTabId);
        Assert.Equal([first.Id, second.Id], graph.Tabs.Select(tab => tab.Id));
        Assert.All(graph.Tabs, tab =>
        {
            var panel = Assert.Single(tab.Panels);
            Assert.Equal(PanelKind.Placeholder, panel.Kind);
            Assert.Equal(tab.ActivePanelId, panel.Id);
        });
    }

    [Fact]
    public void Topology_requires_order_kind_title_and_exact_identities()
    {
        var original = RuntimeWorkspaceGraphProjection.Capture(CreateWorkspace());
        var originalPanel = original.Tabs[0].Panels[0];
        var renamedPanel = ReplacePanel(
            original,
            new PanelInstance(
                originalPanel.Id,
                originalPanel.Kind,
                "Untrusted rename"));
        var replacedIdentity = ReplacePanel(
            original,
            new PanelInstance(
                PanelInstanceId.New(),
                originalPanel.Kind,
                originalPanel.Title));
        var reorderedTabs = new WorkspaceInstance(
            original.Id,
            original.Title,
            original.Tabs.Reverse(),
            original.ActiveTabId);

        Assert.True(RuntimeWorkspaceGraphProjection.TopologyMatches(original, original));
        Assert.False(RuntimeWorkspaceGraphProjection.TopologyMatches(original, renamedPanel));
        Assert.False(RuntimeWorkspaceGraphProjection.TopologyMatches(original, replacedIdentity));
        Assert.False(RuntimeWorkspaceGraphProjection.TopologyMatches(original, reorderedTabs));
    }

    [Fact]
    public void Intent_adds_exact_focus_to_topology_matching()
    {
        var original = RuntimeWorkspaceGraphProjection.Capture(CreateWorkspace());
        var otherTab = original.Tabs[1];
        var changedFocus = new WorkspaceInstance(
            original.Id,
            original.Title,
            original.Tabs,
            otherTab.Id);

        Assert.True(RuntimeWorkspaceGraphProjection.TopologyMatches(original, changedFocus));
        Assert.False(RuntimeWorkspaceGraphProjection.IntentMatches(original, changedFocus));
    }

    [Fact]
    public void Capture_rejects_a_workspace_without_an_active_tab()
    {
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Empty",
            "#123456",
            []);

        Assert.Throws<InvalidOperationException>(
            () => RuntimeWorkspaceGraphProjection.Capture(workspace));
    }

    private static RuntimeWorkspaceViewModel CreateWorkspace()
    {
        var workspace = new RuntimeWorkspaceViewModel(
            WorkspaceInstanceId.New(),
            "Runtime",
            "#123456",
            []);
        var first = CreateTab("First");
        var second = CreateTab("Second");
        workspace.Tabs.Add(first);
        workspace.Tabs.Add(second);
        workspace.ActiveTab = first;
        return workspace;
    }

    private static RuntimeTabViewModel CreateTab(string title)
    {
        var tab = new RuntimeTabViewModel(TabInstanceId.New(), title, "test");
        tab.AddPanel(new PanelPlaceholderViewModel(PanelInstanceId.New()));
        return tab;
    }

    private static WorkspaceInstance ReplacePanel(
        WorkspaceInstance workspace,
        PanelInstance panel)
    {
        var tab = workspace.Tabs[0];
        var replacement = new TabInstance(tab.Id, tab.Title, [panel], panel.Id);
        return new WorkspaceInstance(
            workspace.Id,
            workspace.Title,
            [replacement, .. workspace.Tabs.Skip(1)],
            workspace.ActiveTabId);
    }
}
