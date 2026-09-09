using System.Reflection;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentStatisticsReadActionComposerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationNarrowsBroadScopeToExactStatisticsPanel(
        bool workspaceScope)
    {
        var graph = Graph();
        AgentTarget target = workspaceScope
            ? new AgentTarget.Workspace(Window(), Workspace())
            : new AgentTarget.OpenTab(Window(), Workspace(), Tab());
        var request = new AgentStatisticsReadRequest(StatisticsPanel());

        var action = new AgentStatisticsReadActionComposer().Prepare(
            Envelope(),
            Context(graph, target),
            request);

        Assert.Same(request, action.Request);
        Assert.Equal(BuiltInAgentTools.StatisticsRead, action.Proposal.ToolName);
        Assert.Equal(ExactPanel(), action.Proposal.Target);
        Assert.Equal(
            AgentTargetIdentity.Create(ExactPanel()),
            action.Proposal.TargetIdentity);
        var argument = Assert.Single(action.Proposal.Presentation.Arguments);
        Assert.Equal("panel_id", argument.Name);
        Assert.Equal(StatisticsPanel().Value, argument.DisplayValue);
    }

    [Fact]
    public void BindingUsesFreshRevisionButPreservesTypedRequestDigest()
    {
        var composer = new AgentStatisticsReadActionComposer();
        var action = composer.Prepare(
            Envelope(),
            ExactContext(graphRevision: 11, sessionRevision: 17),
            new AgentStatisticsReadRequest(StatisticsPanel()));

        var binding = composer.BindForExecution(
            action,
            ExactContext(graphRevision: 12, sessionRevision: 18));

        Assert.Equal(ExactPanel(), binding.Target);
        Assert.NotEqual(
            action.Proposal.TargetFingerprint,
            binding.TargetFingerprint);
        Assert.Equal(
            action.Proposal.ArgumentDigest,
            binding.ArgumentDigest);
    }

    [Fact]
    public void PreparationAndBindingRejectWrongKindLifecycleOrCapability()
    {
        var composer = new AgentStatisticsReadActionComposer();
        var request = new AgentStatisticsReadRequest(StatisticsPanel());

        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            ExactContext(kind: PanelKind.ProcessMonitor),
            request));
        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            ExactContext(lifecycle: SessionLifecycle.Starting),
            request));
        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            ExactContext(includeCapability: false),
            request));

        var action = composer.Prepare(
            Envelope(),
            ExactContext(),
            request);
        Assert.Throws<ArgumentException>(() => composer.BindForExecution(
            action,
            ExactContext(includeCapability: false)));
    }

    [Fact]
    public void ProjectionCopiesOnlyValidatedNumericStatistics()
    {
        var composer = new AgentStatisticsReadActionComposer();
        var action = composer.Prepare(
            Envelope(),
            ExactContext(),
            new AgentStatisticsReadRequest(StatisticsPanel()));
        var snapshot = new SystemStatisticsSnapshot(
            Now,
            TimeSpan.FromHours(2),
            LogicalProcessorCount: 8,
            EnumeratedProcessCount: 41,
            ObservedProcessCount: 39,
            ObservedCpuPercent: 12.5,
            ObservedWorkingSetBytes: 4096,
            NetworkReceivedBytesPerSecond: 100.25,
            NetworkSentBytesPerSecond: 50.5);

        var result = composer.Project(action, snapshot);

        Assert.Equal(snapshot.CapturedAtUtc, result.CapturedAtUtc);
        Assert.Equal(snapshot.HostUptime, result.HostUptime);
        Assert.Equal(8, result.LogicalProcessorCount);
        Assert.Equal(41, result.EnumeratedProcessCount);
        Assert.Equal(39, result.ObservedProcessCount);
        Assert.Equal(12.5, result.ObservedCpuPercent);
        Assert.Equal(4096, result.ObservedWorkingSetBytes);
        Assert.Equal(100.25, result.NetworkReceivedBytesPerSecond);
        Assert.Equal(50.5, result.NetworkSentBytesPerSecond);
    }

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public void ProjectionRejectsImpossibleOrNonFiniteCounters(
        SystemStatisticsSnapshot snapshot)
    {
        var composer = new AgentStatisticsReadActionComposer();
        var action = composer.Prepare(
            Envelope(),
            ExactContext(),
            new AgentStatisticsReadRequest(StatisticsPanel()));

        Assert.Throws<ArgumentException>(() => composer.Project(action, snapshot));
    }

    [Fact]
    public void CatalogUsesDedicatedReadOnlySystemDataCapability()
    {
        Assert.True(BuiltInAgentTools.Catalog.TryGet(
            BuiltInAgentTools.StatisticsRead,
            out var descriptor));
        Assert.Equal(AgentCapability.SystemData, descriptor!.Capability);
        Assert.Equal(AgentActionRisk.Observation, descriptor.Risk);
        Assert.Equal(
            AgentPermission.Off,
            AgentPolicy.Default.GetPermission(AgentCapability.SystemData));
        Assert.DoesNotContain(
            "Docker",
            descriptor.Title,
            StringComparison.OrdinalIgnoreCase);

        Assert.All(
            typeof(AgentStatisticsReadResult)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.Null(property.SetMethod));
        Assert.Single(typeof(IAgentStatisticsSessionHost).GetMethods());
    }

    public static IEnumerable<object[]> InvalidSnapshots()
    {
        yield return [Snapshot(capturedAt: Now.ToOffset(TimeSpan.FromHours(1)))];
        yield return [Snapshot(uptime: TimeSpan.FromTicks(-1))];
        yield return [Snapshot(processors: 0)];
        yield return [Snapshot(enumerated: -1)];
        yield return [Snapshot(enumerated: 1, observed: 2)];
        yield return [Snapshot(cpu: double.NaN)];
        yield return [Snapshot(cpu: 101)];
        yield return [Snapshot(workingSet: -1)];
        yield return [Snapshot(received: double.PositiveInfinity)];
        yield return [Snapshot(sent: -1)];
    }

    private static SystemStatisticsSnapshot Snapshot(
        DateTimeOffset? capturedAt = null,
        TimeSpan? uptime = null,
        int processors = 4,
        int enumerated = 2,
        int observed = 1,
        double? cpu = 10,
        long workingSet = 1024,
        double? received = 1,
        double? sent = 1) =>
        new(
            capturedAt ?? Now,
            uptime ?? TimeSpan.FromMinutes(1),
            processors,
            enumerated,
            observed,
            cpu,
            workingSet,
            received,
            sent);

    private static AgentContextSnapshot ExactContext(
        long graphRevision = 11,
        long sessionRevision = 17,
        SessionLifecycle lifecycle = SessionLifecycle.Active,
        PanelKind kind = PanelKind.Statistics,
        bool includeCapability = true)
    {
        var graph = Graph(graphRevision, kind);
        return new AgentContextSnapshot(
            ExactPanel(),
            [AgentContextPanel.ForGraphPanel(
                graph,
                Tab(),
                StatisticsPanel(),
                Descriptor(
                    sessionRevision,
                    lifecycle,
                    kind,
                    includeCapability))],
            Now);
    }

    private static AgentContextSnapshot Context(
        WorkspaceGraphSnapshot graph,
        AgentTarget target) =>
        new(
            target,
            [AgentContextPanel.ForGraphPanel(
                graph,
                Tab(),
                StatisticsPanel(),
                Descriptor())],
            Now);

    private static WorkspaceGraphSnapshot Graph(
        long revision = 11,
        PanelKind kind = PanelKind.Statistics)
    {
        var panel = new PanelInstance(
            StatisticsPanel(),
            kind,
            "Statistics",
            StatisticsSession());
        var tab = new TabInstance(Tab(), "Local", [panel], panel.Id);
        return new WorkspaceGraphSnapshot(
            Window(),
            new WorkspaceInstance(
                Workspace(),
                "Operations",
                [tab],
                tab.Id),
            revision,
            revision);
    }

    private static SessionDescriptor Descriptor(
        long revision = 17,
        SessionLifecycle lifecycle = SessionLifecycle.Active,
        PanelKind kind = PanelKind.Statistics,
        bool includeCapability = true) =>
        new(
            StatisticsSession(),
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
                StatisticsPanel()),
            includeCapability
                ? new CapabilitySet([SessionCapabilities.StatisticsRead])
                : CapabilitySet.Empty,
            revision,
            HasActiveWork: false,
            StatusDetail: "Ready");

    private static AgentActionEnvelope Envelope() =>
        new(
            new AgentActionId("statistics-action"),
            new AgentRunId("statistics-run"),
            new ActorDescriptor(
                new ActorId("statistics-agent"),
                ActorKind.Agent,
                "Statistics agent"),
            policyGeneration: 3,
            Now,
            Now.AddMinutes(1));

    private static AgentTarget.Panel ExactPanel() =>
        new(Window(), Workspace(), Tab(), StatisticsPanel());

    private static WindowInstanceId Window() => new("statistics-window");

    private static WorkspaceInstanceId Workspace() =>
        new("statistics-workspace");

    private static TabInstanceId Tab() => new("statistics-tab");

    private static PanelInstanceId StatisticsPanel() =>
        new("statistics-panel");

    private static SessionId StatisticsSession() =>
        new("statistics-session");
}
