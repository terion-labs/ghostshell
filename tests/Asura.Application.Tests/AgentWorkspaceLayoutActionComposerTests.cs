using System.Reflection;
using Asura.Application;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentWorkspaceLayoutActionComposerTests
{
    [Fact]
    public void Closed_requests_bind_each_tool_to_the_exact_workspace_topology()
    {
        var composer = new AgentWorkspaceLayoutActionComposer();
        var context = Context();
        AgentWorkspaceLayoutRequest[] requests =
        [
            new AgentWorkspaceLayoutRequest.ConnectionList(),
            new AgentWorkspaceLayoutRequest.PanelConnect(
                Panel(),
                "connection_test"),
            new AgentWorkspaceLayoutRequest.TabCreate(PanelKind.Browser),
            new AgentWorkspaceLayoutRequest.TabClose(Tab()),
            new AgentWorkspaceLayoutRequest.PanelAdd(Tab(), PanelKind.FileViewer),
            new AgentWorkspaceLayoutRequest.PanelSplit(
                Panel(),
                AgentPanelSplitOrientation.TopBottom,
                PanelKind.Terminal),
            new AgentWorkspaceLayoutRequest.PanelClose(Panel()),
        ];
        string[] names =
        [
            BuiltInAgentTools.ConnectionsList,
            BuiltInAgentTools.PanelConnect,
            BuiltInAgentTools.TabCreate,
            BuiltInAgentTools.TabClose,
            BuiltInAgentTools.PanelAdd,
            BuiltInAgentTools.PanelSplit,
            BuiltInAgentTools.PanelClose,
        ];

        for (var index = 0; index < requests.Length; index++)
        {
            var action = composer.Prepare(Envelope(), context, requests[index]);
            var binding = composer.BindForExecution(action, context);

            Assert.Equal(names[index], action.Proposal.ToolName);
            Assert.Equal(names[index], binding.ToolName);
            Assert.Equal(action.Proposal.TargetFingerprint, binding.TargetFingerprint);
            Assert.Equal(action.Proposal.ArgumentDigest, binding.ArgumentDigest);
        }
    }

    [Fact]
    public void Layout_rejects_partial_scope_unknown_targets_and_topology_drift()
    {
        var composer = new AgentWorkspaceLayoutActionComposer();
        var context = Context();
        var action = composer.Prepare(
            Envelope(),
            context,
            new AgentWorkspaceLayoutRequest.PanelClose(Panel()));

        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            new AgentContextSnapshot(
                new AgentTarget.Panel(Window(), Workspace(), Tab(), Panel()),
                context.Panels,
                DateTimeOffset.UnixEpoch),
            new AgentWorkspaceLayoutRequest.PanelClose(Panel())));
        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            context,
            new AgentWorkspaceLayoutRequest.PanelClose(
                new PanelInstanceId("outside-panel"))));

        var changed = Context(
            panelId: new PanelInstanceId("replacement-panel"),
            revision: 2);
        Assert.Throws<ArgumentException>(() =>
            composer.BindForExecution(action, changed));
    }

    [Fact]
    public void Request_union_and_creatable_panel_kinds_are_closed()
    {
        Assert.Equal(
            [
                typeof(AgentWorkspaceLayoutRequest.ConnectionList),
                typeof(AgentWorkspaceLayoutRequest.PanelAdd),
                typeof(AgentWorkspaceLayoutRequest.PanelClose),
                typeof(AgentWorkspaceLayoutRequest.PanelConnect),
                typeof(AgentWorkspaceLayoutRequest.PanelSplit),
                typeof(AgentWorkspaceLayoutRequest.TabClose),
                typeof(AgentWorkspaceLayoutRequest.TabCreate),
            ],
            typeof(AgentWorkspaceLayoutRequest)
                .GetNestedTypes(BindingFlags.Public)
                .Where(type => !type.IsAbstract)
                .OrderBy(type => type.Name, StringComparer.Ordinal));
        Assert.True(AgentWorkspaceLayoutRequest.IsCreatableKind(PanelKind.Docker));
        Assert.False(AgentWorkspaceLayoutRequest.IsCreatableKind((PanelKind)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentWorkspaceLayoutRequest.TabCreate((PanelKind)999));
    }

    private static AgentContextSnapshot Context(
        PanelInstanceId? panelId = null,
        long revision = 1)
    {
        var selectedPanel = panelId ?? Panel();
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            new WorkspaceInstance(
                Workspace(),
                "Workspace",
                [new TabInstance(
                    Tab(),
                    "Tab",
                    [new PanelInstance(selectedPanel, PanelKind.Statistics, "Panel")],
                    selectedPanel)],
                Tab()),
            revision,
            lastSequence: revision);
        return new AgentContextSnapshot(
            new AgentTarget.Workspace(Window(), Workspace()),
            [AgentContextPanel.ForGraphPanel(graph, Tab(), selectedPanel, null)],
            DateTimeOffset.UnixEpoch);
    }

    private static AgentActionEnvelope Envelope() => new(
        AgentActionId.New(),
        new AgentRunId("layout-run"),
        new ActorDescriptor(
            new ActorId("layout-agent"),
            ActorKind.Agent,
            "Layout agent"),
        policyGeneration: 1,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch.AddMinutes(1));

    private static WindowInstanceId Window() => new("layout-window");
    private static WorkspaceInstanceId Workspace() => new("layout-workspace");
    private static TabInstanceId Tab() => new("layout-tab");
    private static PanelInstanceId Panel() => new("layout-panel");
}
