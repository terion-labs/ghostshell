using System.Collections.Immutable;
using Asura.Application;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class GovernedAgentSteeringTests
{
    [Fact]
    public void Steering_copies_one_bounded_update_for_an_exact_run()
    {
        var steering = new GovernedAgentSteering(
            new AgentRunId("run-1"),
            expectedGeneration: 7,
            "Use the staging host instead.");

        Assert.Equal(new AgentRunId("run-1"), steering.RunId);
        Assert.Equal(7, steering.ExpectedGeneration);
        Assert.Equal("Use the staging host instead.", steering.Update);
    }

    [Fact]
    public void Steering_rejects_missing_or_oversized_input()
    {
        Assert.Throws<ArgumentException>(
            () => new GovernedAgentSteering(
                default,
                expectedGeneration: 1,
                "Continue."));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new GovernedAgentSteering(
                new AgentRunId("run-1"),
                expectedGeneration: 0,
                "Continue."));
        Assert.Throws<ArgumentException>(
            () => new GovernedAgentSteering(
                new AgentRunId("run-1"),
                expectedGeneration: 1,
                " "));
        Assert.Throws<ArgumentException>(
            () => new GovernedAgentSteering(
                new AgentRunId("run-1"),
                expectedGeneration: 1,
                new string(
                    'x',
                    GovernedAgentSteering.MaximumUpdateLength + 1)));
    }

    [Fact]
    public void Snapshot_allows_steering_only_in_an_unblocked_streaming_state()
    {
        var available = Snapshot(
            GovernedAgentState.StreamingProvider,
            steeringAvailable: true,
            steeringGeneration: 7);

        Assert.True(available.SteeringAvailable);
        Assert.Equal(7, available.SteeringGeneration);
        Assert.True(available.CanSteer);
        Assert.False(
            Snapshot(
                GovernedAgentState.Ready,
                steeringAvailable: true,
                steeringGeneration: 7).CanSteer);
        Assert.False(
            Snapshot(
                GovernedAgentState.StreamingProvider,
                steeringAvailable: false,
                steeringGeneration: 7).CanSteer);
        Assert.False(
            Snapshot(
                GovernedAgentState.StreamingProvider,
                steeringAvailable: true,
                steeringGeneration: null).CanSteer);
        Assert.False(
            Snapshot(
                GovernedAgentState.StreamingProvider,
                steeringAvailable: true,
                steeringGeneration: 7,
                activeTool: new GovernedAgentToolActivity(
                    "terminal.read_screen",
                    "Read screen",
                    AgentActionRisk.Observation,
                    "Terminal")).CanSteer);
    }

    private static GovernedAgentSnapshot Snapshot(
        GovernedAgentState state,
        bool steeringAvailable,
        long? steeringGeneration,
        GovernedAgentToolActivity? activeTool = null) =>
        new(
            state,
            RunId: new AgentRunId("run-1"),
            ProviderId: new AiProviderProfileId("provider-1"),
            Target: null,
            TargetTitle: "Terminal",
            ContextItems: [],
            Messages: [],
            EffectivePolicy: AgentPolicy.Default,
            ProvisionalAssistantText: string.Empty,
            Status: "Waiting",
            ActiveTool: activeTool,
            SteeringAvailable: steeringAvailable,
            SteeringGeneration: steeringGeneration);
}
