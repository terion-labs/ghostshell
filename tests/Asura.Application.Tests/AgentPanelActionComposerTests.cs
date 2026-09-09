using System.Reflection;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentPanelActionComposerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, BuiltInAgentTools.PanelInspect)]
    [InlineData(false, BuiltInAgentTools.PanelFocus)]
    public void Closed_request_kinds_map_to_trusted_tools_and_narrow_broad_scope(
        bool inspect,
        string expectedTool)
    {
        var selected = SecondPanel();
        AgentPanelRequest request = inspect
            ? new AgentPanelRequest.Inspect(selected)
            : new AgentPanelRequest.Focus(selected);
        var context = BroadContext();

        var action = new AgentPanelActionComposer().Prepare(
            Envelope(),
            context,
            request);

        Assert.Same(request, action.Request);
        Assert.Equal(expectedTool, action.Proposal.ToolName);
        Assert.Equal(
            new AgentTarget.Panel(
                Window(),
                Workspace(),
                Tab(),
                selected),
            action.Proposal.Target);
        Assert.Equal(
            AgentTargetIdentity.Create(action.Proposal.Target),
            action.Proposal.TargetIdentity);
        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("panel_id", selected.Value),
                (argument.Name, argument.DisplayValue)));
    }

    [Fact]
    public void Exact_session_scope_is_preserved_and_fresh_binding_tracks_revision()
    {
        var composer = new AgentPanelActionComposer();
        var target = new AgentTarget.ConnectionSession(SecondSession());
        var action = composer.Prepare(
            Envelope(),
            ExactContext(
                SecondPanel(),
                SecondSession(),
                target,
                graphRevision: 11,
                sessionRevision: 17),
            new AgentPanelRequest.Inspect(SecondPanel()));

        var binding = composer.BindForExecution(
            action,
            ExactContext(
                SecondPanel(),
                SecondSession(),
                target,
                graphRevision: 12,
                sessionRevision: 18));

        Assert.Equal(target, action.Proposal.Target);
        Assert.Equal(target, binding.Target);
        Assert.NotEqual(
            action.Proposal.TargetFingerprint,
            binding.TargetFingerprint);
        Assert.Equal(
            action.Proposal.ArgumentDigest,
            binding.ArgumentDigest);
    }

    [Fact]
    public void Preparation_rejects_stale_out_of_scope_and_inactive_panels()
    {
        var composer = new AgentPanelActionComposer();

        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            ExactContext(
                FirstPanel(),
                FirstSession(),
                new AgentTarget.Panel(
                    Window(),
                    Workspace(),
                    Tab(),
                    FirstPanel())),
            new AgentPanelRequest.Focus(SecondPanel())));

        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            ExactContext(
                FirstPanel(),
                FirstSession(),
                new AgentTarget.Panel(
                    Window(),
                    Workspace(),
                    Tab(),
                    FirstPanel()),
                lifecycle: SessionLifecycle.Starting),
            new AgentPanelRequest.Inspect(FirstPanel())));
    }

    [Fact]
    public void Replaced_panel_session_changes_the_fresh_execution_binding()
    {
        var composer = new AgentPanelActionComposer();
        var target = new AgentTarget.Panel(
            Window(),
            Workspace(),
            Tab(),
            FirstPanel());
        var action = composer.Prepare(
            Envelope(),
            ExactContext(FirstPanel(), FirstSession(), target),
            new AgentPanelRequest.Focus(FirstPanel()));

        var binding = composer.BindForExecution(
            action,
            ExactContext(
                FirstPanel(),
                new SessionId("replacement-session"),
                target));

        Assert.NotEqual(
            action.Proposal.TargetFingerprint,
            binding.TargetFingerprint);
    }

    [Fact]
    public void Panel_request_result_and_host_port_have_closed_typed_shapes()
    {
        var requestKinds = typeof(AgentPanelRequest)
            .GetNestedTypes(BindingFlags.Public)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        var resultKinds = typeof(AgentPanelActionResult)
            .GetNestedTypes(BindingFlags.Public)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        var hostMethod = Assert.Single(
            typeof(IAgentPanelSessionHost).GetMethods());

        Assert.True(typeof(AgentPanelRequest).IsAbstract);
        Assert.Equal(
            ["Focus", "Inspect"],
            requestKinds.Select(type => type.Name), StringComparer.Ordinal);
        Assert.All(requestKinds, type => Assert.True(type.IsSealed));
        Assert.All(
            requestKinds,
            type => Assert.Equal(
                typeof(PanelInstanceId),
                Assert.Single(type.GetProperties()).PropertyType));
        Assert.True(typeof(AgentPanelActionResult).IsAbstract);
        Assert.Equal(
            ["Focused", "Inspected"],
            resultKinds.Select(type => type.Name), StringComparer.Ordinal);
        Assert.All(resultKinds, type => Assert.True(type.IsSealed));
        Assert.Empty(typeof(AgentPanelAction).GetConstructors());
        Assert.Equal("RunAgentPanelActionAsync", hostMethod.Name);
        Assert.DoesNotContain(
            hostMethod.GetParameters(),
            parameter => parameter.ParameterType == typeof(object));
    }

    [Fact]
    public void Built_in_panel_tools_have_minimum_required_capabilities()
    {
        Assert.True(BuiltInAgentTools.Catalog.TryGet(
            BuiltInAgentTools.PanelInspect,
            out var inspect));
        Assert.Equal(AgentCapability.Search, inspect!.Capability);
        Assert.Equal(AgentActionRisk.Observation, inspect.Risk);

        Assert.True(BuiltInAgentTools.Catalog.TryGet(
            BuiltInAgentTools.PanelFocus,
            out var focus));
        Assert.Equal(AgentCapability.RunCommands, focus!.Capability);
        Assert.Equal(AgentActionRisk.Routine, focus.Risk);
    }

    private static AgentActionEnvelope Envelope() =>
        new(
            new AgentActionId("panel-action-1"),
            new AgentRunId("panel-run-1"),
            new ActorDescriptor(
                new ActorId("panel-agent-1"),
                ActorKind.Agent,
                "Panel agent"),
            policyGeneration: 3,
            Now,
            Now.AddMinutes(1));

    private static AgentContextSnapshot BroadContext()
    {
        var graph = Graph(
            activePanelId: FirstPanel(),
            graphRevision: 11);
        return new AgentContextSnapshot(
            new AgentTarget.Workspace(Window(), Workspace()),
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    FirstPanel(),
                    Descriptor(
                        FirstPanel(),
                        FirstSession(),
                        sessionRevision: 17)),
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    SecondPanel(),
                    Descriptor(
                        SecondPanel(),
                        SecondSession(),
                        sessionRevision: 19)),
            ],
            Now);
    }

    private static AgentContextSnapshot ExactContext(
        PanelInstanceId panelId,
        SessionId sessionId,
        AgentTarget target,
        long graphRevision = 11,
        long sessionRevision = 17,
        SessionLifecycle lifecycle = SessionLifecycle.Active)
    {
        var graph = Graph(
            activePanelId: FirstPanel(),
            graphRevision,
            firstSessionId: panelId == FirstPanel()
                ? sessionId
                : null,
            secondSessionId: panelId == SecondPanel()
                ? sessionId
                : null);
        return new AgentContextSnapshot(
            target,
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    panelId,
                    Descriptor(
                        panelId,
                        sessionId,
                        sessionRevision,
                        lifecycle)),
            ],
            Now);
    }

    private static WorkspaceGraphSnapshot Graph(
        PanelInstanceId activePanelId,
        long graphRevision,
        SessionId? firstSessionId = null,
        SessionId? secondSessionId = null)
    {
        var first = new PanelInstance(
            FirstPanel(),
            PanelKind.Terminal,
            "Primary",
            firstSessionId ?? FirstSession());
        var second = new PanelInstance(
            SecondPanel(),
            PanelKind.Browser,
            "Reference",
            secondSessionId ?? SecondSession());
        var tab = new TabInstance(
            Tab(),
            "Work",
            [first, second],
            activePanelId);
        return new WorkspaceGraphSnapshot(
            Window(),
            new WorkspaceInstance(
                Workspace(),
                "Operations",
                [tab],
                tab.Id),
            graphRevision,
            graphRevision);
    }

    private static SessionDescriptor Descriptor(
        PanelInstanceId panelId,
        SessionId sessionId,
        long sessionRevision,
        SessionLifecycle lifecycle = SessionLifecycle.Active)
    {
        var kind = panelId == FirstPanel()
            ? PanelKind.Terminal
            : PanelKind.Browser;
        return new SessionDescriptor(
            sessionId,
            kind,
            lifecycle,
            lifecycle == SessionLifecycle.Active
                ? SessionHealth.Healthy
                : SessionHealth.Starting,
            new SessionOwner(
                HostMode.Desktop,
                Window(),
                Workspace(),
                Tab(),
                panelId),
            CapabilitySet.Empty,
            sessionRevision,
            HasActiveWork: false,
            StatusDetail: "Ready");
    }

    private static WindowInstanceId Window() => new("panel-window-1");

    private static WorkspaceInstanceId Workspace() =>
        new("panel-workspace-1");

    private static TabInstanceId Tab() => new("panel-tab-1");

    private static PanelInstanceId FirstPanel() => new("panel-1");

    private static PanelInstanceId SecondPanel() => new("panel-2");

    private static SessionId FirstSession() => new("session-1");

    private static SessionId SecondSession() => new("session-2");
}
