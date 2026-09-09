using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task WaitForChangeProposalUsesTheClosedTypedHostRequest()
    {
        await using var fixture = new RuntimeFixture(
            WaitThenAnswer(
                """{"after_content_revision":7,"timeout_ms":1000}"""));
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Wait(
                TerminalWaitOutcome.Changed(
                    fixture.Context.Screen("changed", contentRevision: 8),
                    initialContentRevision: 7)));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Wait for the terminal to change."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var request = Assert.IsType<AgentTerminalRequest.WaitForChange>(
            Assert.Single(fixture.Terminal.Actions).Request);
        Assert.Equal(fixture.Context.SessionId, request.Value.SessionId);
        Assert.Equal(7, request.Value.Wait.AfterContentRevision);
        Assert.Equal(TimeSpan.FromSeconds(1), request.Value.Wait.Timeout);
        Assert.Contains(
            "\"wait_outcome\":\"changed\"",
            ToolResult(fixture),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"panel_id\":\"panel-1\"",
            ToolResult(fixture),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForStableProposalUsesTheClosedTypedHostRequest()
    {
        await using var fixture = new RuntimeFixture(
            WaitThenAnswer(
                """{"stable_for_ms":250,"timeout_ms":1000}"""));
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Wait(
                TerminalWaitOutcome.Stable(
                    fixture.Context.Screen("stable", contentRevision: 8),
                    initialContentRevision: 8)));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Wait until the terminal settles."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var request = Assert.IsType<AgentTerminalRequest.WaitForStable>(
            Assert.Single(fixture.Terminal.Actions).Request);
        Assert.Equal(fixture.Context.SessionId, request.Value.SessionId);
        Assert.Equal(TimeSpan.FromMilliseconds(250), request.Value.Wait.StableFor);
        Assert.Equal(TimeSpan.FromSeconds(1), request.Value.Wait.Timeout);
        Assert.Contains(
            "\"wait_outcome\":\"stable\"",
            ToolResult(fixture),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandFinishedProposalUsesBaselineAndReturnsObservedExitCode()
    {
        await using var fixture = new RuntimeFixture(
            WaitThenAnswer(
                """{"command_finished":true,"after_shell_event_sequence":7,"timeout_ms":1000}"""));
        var shellEvent = new TerminalShellIntegrationEvent(
            Sequence: 8,
            TerminalCommandBoundaryKind.CommandFinished,
            DateTimeOffset.UnixEpoch,
            ExitCode: 23);
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Wait(
                TerminalWaitOutcome.CommandFinished(
                    fixture.Context.Screen(
                        "done",
                        contentRevision: 8,
                        shellIntegrationEvents: [shellEvent]),
                    initialContentRevision: 7,
                    shellEvent)));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Wait for the current terminal command to finish."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var request = Assert.IsType<AgentTerminalRequest.WaitForCommandFinished>(
            Assert.Single(fixture.Terminal.Actions).Request);
        Assert.Equal(fixture.Context.SessionId, request.Value.SessionId);
        Assert.Equal(7, request.Value.Wait.AfterShellEventSequence);
        Assert.Equal(TimeSpan.FromSeconds(1), request.Value.Wait.Timeout);
        Assert.Contains(
            "\"wait_outcome\":\"command_finished\"",
            ToolResult(fixture),
            StringComparison.Ordinal);
        Assert.Contains(
            "\"observed_exit_code\":23",
            ToolResult(fixture),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneHourDelayGetsLongLivedProposalAndConsumedPermitOnly()
    {
        await using var fixture = new RuntimeFixture(
            WaitThenAnswer("""{"delay_ms":3600000}"""));
        var snapshot = fixture.Context.Screen("after delay", contentRevision: 9);
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Wait(
                TerminalWaitOutcome.Elapsed(
                    snapshot,
                    initialContentRevision: 8)));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Read the terminal after one hour."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var action = Assert.Single(fixture.Terminal.Actions);
        var request = Assert.IsType<AgentTerminalRequest.WaitForDelay>(
            action.Request);
        Assert.Equal(TimeSpan.FromHours(1), request.Value.Wait.Delay);
        Assert.Equal(
            TimeSpan.FromMinutes(66),
            action.Proposal.DeadlineUtc - action.Proposal.CreatedAtUtc);
        var permit = Assert.Single(fixture.Terminal.Permits);
        Assert.InRange(
            permit.ExecutionDeadlineUtc - permit.StartedAtUtc,
            TimeSpan.FromMinutes(60.9),
            TimeSpan.FromMinutes(61));
        Assert.Contains(
            "\"wait_outcome\":\"elapsed\"",
            ToolResult(fixture),
            StringComparison.Ordinal);
    }

    private static ProviderRound WaitThenAnswer(string arguments) =>
        new((call, request) => call switch
        {
            1 =>
            [
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.ToolCallStarted(
                    0,
                    "provider-wait",
                    ProviderToolName.FromInternal(BuiltInAgentTools.TerminalWait)),
                new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments),
                new AgentProviderEvent.ToolCallCompleted(0),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.ToolUse),
            ],
            2 when request.Messages.Any(
                message => message.Role == AgentMessageRole.Tool) =>
            [
                new AgentProviderEvent.ResponseStarted(),
                new AgentProviderEvent.TextDelta("The terminal wait completed."),
                new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.EndTurn),
            ],
            _ => throw new InvalidOperationException(
                "The wait provider received an unexpected round."),
        });

    private static string ToolResult(RuntimeFixture fixture)
    {
        var continuation = fixture.Provider.Requests.ToArray()[1];
        return Assert.Single(
                continuation.Messages,
                message => message.Role == AgentMessageRole.Tool)
            .ToolResult!
            .Value
            .Content;
    }
}
