using System.Globalization;
using System.Text.Json;

namespace Asura.Core.Tests;

public sealed class RuntimeInstanceTests
{
    [Fact]
    public void Runtime_graph_copies_input_collections_and_normalizes_titles()
    {
        var panels = new List<PanelInstance>
        {
            new(new PanelInstanceId("panel-1"), PanelKind.Terminal, " Terminal "),
        };
        var tab = new TabInstance(
            new TabInstanceId("tab-1"),
            " Primary ",
            panels,
            panels[0].Id);
        var tabs = new List<TabInstance> { tab };
        var workspace = new WorkspaceInstance(
            new WorkspaceInstanceId("workspace-1"),
            " Operations ",
            tabs,
            tab.Id);

        panels.Clear();
        tabs.Clear();

        Assert.Equal("Operations", workspace.Title);
        Assert.Equal("Primary", workspace.Tabs[0].Title);
        Assert.Equal("Terminal", workspace.Tabs[0].Panels[0].Title);
        Assert.Single(workspace.Tabs);
        Assert.Single(workspace.Tabs[0].Panels);

        var copy = new WorkspaceInstance(workspace);
        Assert.NotSame(workspace, copy);
        Assert.NotSame(workspace.Tabs, copy.Tabs);
        Assert.NotSame(workspace.Tabs[0], copy.Tabs[0]);
        Assert.NotSame(workspace.Tabs[0].Panels[0], copy.Tabs[0].Panels[0]);
    }

    [Fact]
    public void Runtime_graph_rejects_duplicate_and_unowned_ids()
    {
        var duplicatePanelId = new PanelInstanceId("panel-duplicate");
        var first = Tab(
            "tab-1",
            new PanelInstance(duplicatePanelId, PanelKind.Terminal, "First"));
        var second = Tab(
            "tab-2",
            new PanelInstance(duplicatePanelId, PanelKind.Browser, "Second"));

        Assert.Throws<ArgumentException>(() => new WorkspaceInstance(
            new WorkspaceInstanceId("workspace-1"),
            "Workspace",
            [first, second],
            first.Id));
        Assert.Throws<ArgumentException>(() => new WorkspaceInstance(
            new WorkspaceInstanceId("workspace-1"),
            "Workspace",
            [first, new TabInstance(first)],
            first.Id));
        Assert.Throws<ArgumentException>(() => new TabInstance(
            new TabInstanceId("tab-3"),
            "Broken",
            first.Panels,
            new PanelInstanceId("missing-panel")));
        Assert.Throws<ArgumentException>(() => new WorkspaceInstance(
            new WorkspaceInstanceId("workspace-1"),
            "Workspace",
            [first],
            new TabInstanceId("missing-tab")));
    }

    [Fact]
    public void Runtime_graph_accepts_the_complete_panel_boundary_and_rejects_overflow()
    {
        var panels = Enumerable.Range(1, WorkspaceInstance.MaximumPanelCount)
            .Select(index => new PanelInstance(
                new PanelInstanceId($"panel-{index}"),
                PanelKind.Terminal,
                $"Panel {index}"))
            .ToArray();
        var tab = Tab("tab-1", panels);

        var workspace = new WorkspaceInstance(
            new WorkspaceInstanceId("workspace-1"),
            "Workspace",
            [tab],
            tab.Id);

        Assert.Equal(WorkspaceInstance.MaximumPanelCount, workspace.Tabs[0].Panels.Count);

        var overflow = panels
            .Append(new PanelInstance(
                new PanelInstanceId("panel-overflow"),
                PanelKind.Browser,
                "Overflow"))
            .ToArray();

        var exception = Assert.Throws<ArgumentException>(() => new WorkspaceInstance(
            new WorkspaceInstanceId("workspace-2"),
            "Overflow",
            [Tab("tab-overflow", overflow)],
            new TabInstanceId("tab-overflow")));

        Assert.Contains(
            WorkspaceInstance.MaximumPanelCount.ToString(CultureInfo.InvariantCulture),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Activation_targets_stable_ids_and_preserves_source_projection()
    {
        var first = Tab(
            "tab-1",
            new PanelInstance(new PanelInstanceId("panel-1"), PanelKind.Terminal, "First"),
            new PanelInstance(new PanelInstanceId("panel-2"), PanelKind.FileViewer, "Files"));
        var second = Tab(
            "tab-2",
            new PanelInstance(new PanelInstanceId("panel-3"), PanelKind.Browser, "Browser"));
        var workspace = new WorkspaceInstance(
            new WorkspaceInstanceId("workspace-1"),
            "Workspace",
            [first, second],
            first.Id);

        var activatedTab = workspace.ActivateTab(second.Id);
        var activatedPanel = activatedTab.ActivatePanel(first.Id, first.Panels[1].Id);

        Assert.Equal(first.Id, workspace.ActiveTabId);
        Assert.Equal(second.Id, activatedTab.ActiveTabId);
        Assert.Equal(first.Id, activatedPanel.ActiveTabId);
        Assert.Equal(first.Panels[1].Id, activatedPanel.Tabs[0].ActivePanelId);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            workspace.ActivateTab(new TabInstanceId("missing-tab")));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            workspace.ActivatePanel(first.Id, second.Panels[0].Id));
    }

    [Fact]
    public void Session_replacement_is_immutable_and_idempotent()
    {
        var terminal = new PanelInstance(
            new PanelInstanceId("panel-1"),
            PanelKind.Terminal,
            "Terminal");
        var tab = Tab("tab-1", terminal);
        var workspace = new WorkspaceInstance(
            new WorkspaceInstanceId("workspace-1"),
            "Workspace",
            [tab],
            tab.Id);
        var sessionId = new SessionId("session-1");

        var linked = workspace.ReplacePanelSession(tab.Id, terminal.Id, sessionId);
        var repeated = linked.ReplacePanelSession(tab.Id, terminal.Id, sessionId);
        var unlinked = linked.ReplacePanelSession(tab.Id, terminal.Id, null);

        Assert.Null(workspace.Tabs[0].Panels[0].SessionId);
        Assert.Equal(sessionId, linked.Tabs[0].Panels[0].SessionId);
        Assert.Same(linked, repeated);
        Assert.Null(unlinked.Tabs[0].Panels[0].SessionId);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            workspace.ReplacePanelSession(
                new TabInstanceId("missing-tab"),
                terminal.Id,
                sessionId));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            workspace.ReplacePanelSession(
                tab.Id,
                new PanelInstanceId("missing-panel"),
                sessionId));
    }

    [Fact]
    public void Runtime_titles_reject_empty_control_and_oversized_values()
    {
        Assert.Throws<ArgumentException>(() => new PanelInstance(
            new PanelInstanceId("panel-1"),
            PanelKind.Terminal,
            "\n"));
        Assert.Throws<ArgumentException>(() => new PanelInstance(
            new PanelInstanceId("panel-1"),
            PanelKind.Terminal,
            new string('x', 201)));
    }

    [Fact]
    public void Runtime_graph_round_trips_through_json_without_losing_identity()
    {
        var panel = new PanelInstance(
            new PanelInstanceId("panel-1"),
            PanelKind.Terminal,
            "Terminal",
            new SessionId("session-1"));
        var tab = Tab("tab-1", panel);
        var workspace = new WorkspaceInstance(
            new WorkspaceInstanceId("workspace-1"),
            "Workspace",
            [tab],
            tab.Id);

        var json = JsonSerializer.Serialize(workspace);
        var restored = JsonSerializer.Deserialize<WorkspaceInstance>(json)!;

        Assert.Equal(workspace.Id, restored.Id);
        Assert.Equal(workspace.ActiveTabId, restored.ActiveTabId);
        Assert.Equal(panel.Id, restored.Tabs[0].Panels[0].Id);
        Assert.Equal(panel.SessionId, restored.Tabs[0].Panels[0].SessionId);
    }

    private static TabInstance Tab(string id, params PanelInstance[] panels) =>
        new(new TabInstanceId(id), id, panels, panels[0].Id);
}
