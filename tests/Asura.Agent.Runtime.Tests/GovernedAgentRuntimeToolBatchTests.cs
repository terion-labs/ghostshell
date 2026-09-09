using Asura.Agent;
using Asura.Application;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task ToolBatchExecutesSuccessfulCallsInProviderSourceOrder()
    {
        var provider = ProviderRound.BatchThenAnswer(
        [
            new(
                "provider-read-first",
                BuiltInAgentTools.TerminalReadScreen,
                "{}"),
            new(
                "provider-read-second",
                BuiltInAgentTools.TerminalReadScreen,
                "{}"),
        ]);
        await using var fixture = new RuntimeFixture(provider);
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(
                fixture.Context.Screen("first", contentRevision: 1)));
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(
                fixture.Context.Screen("second", contentRevision: 2)));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Read the terminal twice."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, fixture.Terminal.Actions.Count);
        Assert.Equal(2, provider.Requests.Count);
        var continuation = provider.Requests.ToArray()[1];
        var toolResults = continuation.Messages
            .Where(message => message.Role == AgentMessageRole.Tool)
            .Select(message => Assert.IsType<AgentToolResult>(message.ToolResult))
            .ToArray();
        Assert.Collection(
            toolResults,
            first =>
            {
                Assert.Equal("provider-read-first", first.ProviderCallId);
                Assert.Equal(AgentToolResultStatus.Succeeded, first.Status);
                Assert.Contains("first", first.Value.Content, StringComparison.Ordinal);
            },
            second =>
            {
                Assert.Equal("provider-read-second", second.ProviderCallId);
                Assert.Equal(AgentToolResultStatus.Succeeded, second.Status);
                Assert.Contains("second", second.Value.Content, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task ToolBatchReturnsDeniedAndCancelledResultsTogetherInSourceOrder()
    {
        var provider = ProviderRound.BatchThenAnswer(
        [
            new(
                "provider-denied",
                BuiltInAgentTools.TerminalSendText,
                "{\"text\":\"first\"}"),
            new(
                "provider-cancelled",
                BuiltInAgentTools.TerminalSendText,
                "{\"text\":\"second\"}"),
        ]);
        await using var fixture = new RuntimeFixture(provider);
        fixture.Terminal.HoldBlockedActionAfterCancellation = true;
        fixture.Terminal.BlockNextAction();
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Try both commands."),
            CancellationToken.None).AsTask();
        var deniedApproval = await WaitForNewApprovalAsync(
            fixture.Runtime,
            previousApproval: null);
        Assert.Contains(
            deniedApproval.Presentation.Arguments,
            argument => string.Equals(argument.Name, "text"
, StringComparison.Ordinal) && string.Equals(argument.DisplayValue, "first", StringComparison.Ordinal));

        Assert.True((await fixture.Runtime.DecideAsync(
            deniedApproval.Id,
            approved: false,
            CancellationToken.None)).IsAccepted);
        var cancelledApproval = await WaitForNewApprovalAsync(
            fixture.Runtime,
            deniedApproval.Id);
        Assert.Contains(
            cancelledApproval.Presentation.Arguments,
            argument => string.Equals(argument.Name, "text"
, StringComparison.Ordinal) && string.Equals(argument.DisplayValue, "second", StringComparison.Ordinal));
        Assert.True((await fixture.Runtime.DecideAsync(
            cancelledApproval.Id,
            approved: true,
            CancellationToken.None)).IsAccepted);
        await fixture.Terminal.BlockedActionStarted.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var cancellation = await fixture.Runtime.CancelActiveActionAsync(
                CancellationToken.None);
            await fixture.Terminal.BlockedActionCancellationObserved.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(cancellation.WasRequested);
        }
        finally
        {
            fixture.Terminal.ReleaseBlockedActionCancellation.TrySetResult();
        }

        Assert.True((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        var action = Assert.Single(fixture.Terminal.Actions);
        Assert.Equal(
            "second",
            Assert.IsType<AgentTerminalRequest.SendText>(action.Request).Text);
        Assert.Equal(2, provider.Requests.Count);
        var continuation = provider.Requests.ToArray()[1];
        var toolResults = continuation.Messages
            .Where(message => message.Role == AgentMessageRole.Tool)
            .Select(message => Assert.IsType<AgentToolResult>(message.ToolResult))
            .ToArray();
        Assert.Collection(
            toolResults,
            denied =>
            {
                Assert.Equal("provider-denied", denied.ProviderCallId);
                Assert.Equal("approval_denied", denied.StableCode);
            },
            cancelled =>
            {
                Assert.Equal("provider-cancelled", cancelled.ProviderCallId);
                Assert.Equal("caller_cancelled", cancelled.StableCode);
            });
    }

    [Fact]
    public async Task UncertainFirstToolOutcomeQuarantinesWithoutExecutingBatchRemainder()
    {
        var provider = ProviderRound.BatchThenAnswer(
        [
            new(
                "provider-action-uncertain",
                BuiltInAgentTools.TerminalSendText,
                "{\"text\":\"first\"}"),
            new(
                "provider-read-skipped",
                BuiltInAgentTools.TerminalReadScreen,
                "{}"),
        ]);
        await using var fixture = new RuntimeFixture(provider);
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Completed());
        fixture.Audit.FailurePredicate = auditEvent =>
            auditEvent.Outcome == AuditOutcome.Succeeded;

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Run a command, then read the terminal."),
            CancellationToken.None).AsTask();
        var approval = await WaitForNewApprovalAsync(
            fixture.Runtime,
            previousApproval: null);
        Assert.True((await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: true,
            CancellationToken.None)).IsAccepted);
        var result = await sending;

        Assert.False(result.IsSuccess);
        Assert.Equal(AgentActionFailureCodes.CompletionAuditUnavailable, result.Code);
        Assert.Equal(GovernedAgentState.Failed, fixture.Runtime.Snapshot.State);
        var action = Assert.Single(fixture.Terminal.Actions);
        Assert.Equal(
            "first",
            Assert.IsType<AgentTerminalRequest.SendText>(action.Request).Text);
        Assert.Single(provider.Requests);
    }

    private sealed record ToolBatchCall(
        string ProviderCallId,
        string ToolName,
        string Arguments);

    private sealed partial class ProviderRound
    {
        public static ProviderRound BatchThenAnswer(
            IReadOnlyList<ToolBatchCall> calls) =>
            new((call, _) => call switch
            {
                1 => ToolBatch(calls),
                2 => Answer("Batch completed."),
                _ => throw new InvalidOperationException(
                    "The provider received an unexpected round."),
            });

        internal static AgentProviderEvent[] ToolBatch(
            IReadOnlyList<ToolBatchCall> calls)
        {
            var events = new List<AgentProviderEvent>
            {
                new AgentProviderEvent.ResponseStarted(),
            };
            for (var index = 0; index < calls.Count; index++)
            {
                var toolCall = calls[index];
                events.Add(new AgentProviderEvent.ToolCallStarted(
                    index,
                    toolCall.ProviderCallId,
                    ProviderToolName.FromInternal(toolCall.ToolName)));
                events.Add(new AgentProviderEvent.ToolCallArgumentsDelta(
                    index,
                    toolCall.Arguments));
                events.Add(new AgentProviderEvent.ToolCallCompleted(index));
            }

            events.Add(new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.ToolUse));
            return [.. events];
        }
    }
}
