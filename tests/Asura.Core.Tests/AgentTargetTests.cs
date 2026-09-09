namespace Asura.Core.Tests;

public sealed class AgentTargetTests
{
    [Fact]
    public void Target_variants_preserve_exact_runtime_identity()
    {
        var windowId = new WindowInstanceId("window-1");
        var workspaceId = new WorkspaceInstanceId("workspace-1");
        var panelId = new PanelInstanceId("panel-1");
        var tabId = new TabInstanceId("tab-1");
        var sessionId = new SessionId("session-1");

        var panel = new AgentTarget.Panel(windowId, workspaceId, tabId, panelId);
        var session = new AgentTarget.ConnectionSession(sessionId);
        var tab = new AgentTarget.OpenTab(windowId, workspaceId, tabId);
        var workspace = new AgentTarget.Workspace(windowId, workspaceId);

        Assert.Equal(windowId, panel.WindowId);
        Assert.Equal(workspaceId, panel.WorkspaceId);
        Assert.Equal(tabId, panel.TabId);
        Assert.Equal(panelId, panel.PanelId);
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal(windowId, tab.WindowId);
        Assert.Equal(workspaceId, tab.WorkspaceId);
        Assert.Equal(tabId, tab.TabId);
        Assert.Equal(windowId, workspace.WindowId);
        Assert.Equal(workspaceId, workspace.WorkspaceId);
    }

    [Fact]
    public void Target_variants_reject_default_runtime_identifiers()
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentTarget.Panel(
                default,
                new WorkspaceInstanceId("workspace-1"),
                new TabInstanceId("tab-1"),
                new PanelInstanceId("panel-1")));
        Assert.Throws<ArgumentException>(() =>
            new AgentTarget.Panel(
                new WindowInstanceId("window-1"),
                new WorkspaceInstanceId("workspace-1"),
                new TabInstanceId("tab-1"),
                default));
        Assert.Throws<ArgumentException>(() =>
            new AgentTarget.ConnectionSession(default));
        Assert.Throws<ArgumentException>(() =>
            new AgentTarget.OpenTab(
                new WindowInstanceId("window-1"),
                new WorkspaceInstanceId("workspace-1"),
                default));
        Assert.Throws<ArgumentException>(() =>
            new AgentTarget.Workspace(
                new WindowInstanceId("window-1"),
                default));
    }

    [Fact]
    public void Target_ids_are_printable_and_utf8_bounded()
    {
        Assert.Throws<ArgumentException>(() => new AgentTarget.Workspace(
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId(new string('x', 257))));
        Assert.Throws<ArgumentException>(() => new AgentTarget.Panel(
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId("workspace-1"),
            new TabInstanceId("tab-1"),
            new PanelInstanceId("panel-\n1")));
    }

    [Fact]
    public void Selected_panel_set_is_bounded_canonical_and_defensively_copied()
    {
        var source = new List<AgentTarget.Panel>
        {
            Panel("tab-b", "panel-b"),
            Panel("tab-a", "panel-c"),
            Panel("tab-a", "panel-a"),
        };

        var target = new AgentTarget.SelectedPanels(source);
        source.Clear();

        Assert.Equal(
            [
                ("tab-a", "panel-a"),
                ("tab-a", "panel-c"),
                ("tab-b", "panel-b"),
            ],
            [.. target.Panels.Select(panel => (panel.TabId.Value, panel.PanelId.Value))]);
        var exposed = Assert.IsAssignableFrom<IList<AgentTarget.Panel>>(target.Panels);
        Assert.Throws<NotSupportedException>(() => exposed.Clear());
    }

    [Fact]
    public void Selected_panel_set_has_canonical_value_equality()
    {
        var first = new AgentTarget.SelectedPanels(
        [
            Panel("tab-b", "panel-b"),
            Panel("tab-a", "panel-a"),
        ]);
        var reorderedEquivalent = new AgentTarget.SelectedPanels(
        [
            Panel("tab-a", "panel-a"),
            Panel("tab-b", "panel-b"),
        ]);

        Assert.Equal(first, reorderedEquivalent);
        Assert.True(first == reorderedEquivalent);
        Assert.Equal(first.GetHashCode(), reorderedEquivalent.GetHashCode());
        Assert.Contains(reorderedEquivalent, new HashSet<AgentTarget.SelectedPanels> { first });
    }

    [Fact]
    public void Selected_panel_set_equality_binds_every_panel_identity()
    {
        var baseline = new AgentTarget.SelectedPanels(
        [
            Panel("window-1", "workspace-1", "tab-a", "panel-a"),
            Panel("window-1", "workspace-1", "tab-b", "panel-b"),
        ]);
        var changedTargets = new[]
        {
            new AgentTarget.SelectedPanels(
            [
                Panel("window-2", "workspace-1", "tab-a", "panel-a"),
                Panel("window-2", "workspace-1", "tab-b", "panel-b"),
            ]),
            new AgentTarget.SelectedPanels(
            [
                Panel("window-1", "workspace-2", "tab-a", "panel-a"),
                Panel("window-1", "workspace-2", "tab-b", "panel-b"),
            ]),
            new AgentTarget.SelectedPanels(
            [
                Panel("window-1", "workspace-1", "tab-a", "panel-a"),
                Panel("window-1", "workspace-1", "tab-c", "panel-b"),
            ]),
            new AgentTarget.SelectedPanels(
            [
                Panel("window-1", "workspace-1", "tab-a", "panel-a"),
                Panel("window-1", "workspace-1", "tab-b", "panel-c"),
            ]),
        };

        Assert.All(
            changedTargets,
            changed =>
            {
                Assert.NotEqual(baseline, changed);
                Assert.False(baseline == changed);
            });
    }

    [Fact]
    public void Selected_panel_set_rejects_empty_duplicate_and_oversized_inputs()
    {
        Assert.Throws<ArgumentException>(() => new AgentTarget.SelectedPanels([]));
        Assert.Throws<ArgumentException>(() => new AgentTarget.SelectedPanels(
        [
            Panel("tab-1", "panel-1"),
            Panel("tab-1", "panel-1"),
        ]));
        Assert.Throws<ArgumentException>(() => new AgentTarget.SelectedPanels(
        [
            Panel("tab-1", "panel-1"),
            new AgentTarget.Panel(
                new WindowInstanceId("window-2"),
                new WorkspaceInstanceId("workspace-1"),
                new TabInstanceId("tab-2"),
                new PanelInstanceId("panel-2")),
        ]));
        Assert.Throws<ArgumentException>(() => new AgentTarget.SelectedPanels(
        [
            Panel("tab-1", "panel-1"),
            new AgentTarget.Panel(
                new WindowInstanceId("window-1"),
                new WorkspaceInstanceId("workspace-2"),
                new TabInstanceId("tab-2"),
                new PanelInstanceId("panel-2")),
        ]));
        Assert.Throws<ArgumentException>(() => new AgentTarget.SelectedPanels(
        [
            Panel("tab-1", "panel-1"),
            Panel("tab-2", "panel-1"),
        ]));
        Assert.Throws<ArgumentException>(() => new AgentTarget.SelectedPanels(
            Enumerable.Range(0, AgentTarget.SelectedPanels.MaximumPanelCount + 1)
                .Select(index => Panel("tab-1", $"panel-{index}"))));
    }

    private static AgentTarget.Panel Panel(string tabId, string panelId) =>
        Panel("window-1", "workspace-1", tabId, panelId);

    private static AgentTarget.Panel Panel(
        string windowId,
        string workspaceId,
        string tabId,
        string panelId) =>
        new(
            new WindowInstanceId(windowId),
            new WorkspaceInstanceId(workspaceId),
            new TabInstanceId(tabId),
            new PanelInstanceId(panelId));
}
