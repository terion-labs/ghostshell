using Asura.Application;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentRunAuditTests
{
    private static readonly DateTimeOffset ReferenceTime =
        new(2026, 7, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActionRejectsImpossiblePhaseTransition()
    {
        Assert.Throws<ArgumentException>(() => CreateAction(
            [
                Phase(AuditOutcome.Requested, ReferenceTime),
                Phase(AuditOutcome.Started, ReferenceTime.AddSeconds(1)),
            ]));
    }

    [Fact]
    public void ActionRequiresUtcOrderedPhasesBeginningWithRequested()
    {
        Assert.Throws<ArgumentException>(() => CreateAction(
            [
                Phase(AuditOutcome.Approved, ReferenceTime),
            ]));
        Assert.Throws<ArgumentException>(() => CreateAction(
            [
                Phase(AuditOutcome.Requested, ReferenceTime.AddSeconds(1)),
                Phase(AuditOutcome.Denied, ReferenceTime),
            ]));
        Assert.Throws<ArgumentException>(() => new AgentRunAuditPhase(
            AuditOutcome.Requested,
            ActorKind.Agent,
            ReferenceTime.ToOffset(TimeSpan.FromHours(2))));
    }

    [Fact]
    public void ActionCopiesValidBoundedPhaseSequence()
    {
        var source = new List<AgentRunAuditPhase>
        {
            Phase(AuditOutcome.Requested, ReferenceTime),
            Phase(AuditOutcome.Approved, ReferenceTime.AddSeconds(1)),
            Phase(AuditOutcome.Started, ReferenceTime.AddSeconds(2)),
            Phase(AuditOutcome.Succeeded, ReferenceTime.AddSeconds(3)),
        };

        var action = CreateAction(source);
        source.Clear();

        Assert.Equal(4, action.Phases.Count);
        Assert.Equal(AuditOutcome.Succeeded, action.LatestOutcome);
    }

    [Fact]
    public void PageAndQueryEnforceTheirBounds()
    {
        var action = CreateAction(
            [
                Phase(AuditOutcome.Requested, ReferenceTime),
                Phase(AuditOutcome.Denied, ReferenceTime.AddSeconds(1)),
            ]);

        Assert.Throws<ArgumentException>(() => new AgentRunAuditPage(
            Enumerable.Repeat<AgentRunAuditEntry>(
                action,
                AgentRunAuditQuery.MaximumPageSize + 1),
            next: null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRunAuditQuery(
            new AgentRunId("run-audit"),
            pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRunAuditQuery(
            new AgentRunId("run-audit"),
            pageSize: AgentRunAuditQuery.MaximumPageSize + 1));
    }

    private static AgentRunAuditActionEntry CreateAction(
        IEnumerable<AgentRunAuditPhase> phases) =>
        new(
            AgentActionDigest.FromUtf8("entry"),
            BuiltInAgentTools.TerminalReadScreen,
            AgentCapability.TerminalRead,
            AgentActionRisk.Observation,
            AgentPermission.Auto,
            AgentPolicyDecision.AuthorizedByAuto,
            AgentAuthorizationSource.AutoPolicy,
            errorCode: null,
            resultCode: "terminal_read_succeeded",
            policyGeneration: 2,
            AgentActionDigest.FromUtf8("target"),
            executionDurationMilliseconds: 20,
            resultCount: 1,
            phases);

    private static AgentRunAuditPhase Phase(
        AuditOutcome outcome,
        DateTimeOffset occurredAtUtc) =>
        new(outcome, ActorKind.Agent, occurredAtUtc);
}
