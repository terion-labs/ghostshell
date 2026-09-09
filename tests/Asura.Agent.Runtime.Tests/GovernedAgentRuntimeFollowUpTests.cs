using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeTests
{
    [Fact]
    public async Task QueuedFollowUpRunsAfterCurrentTurnWithItsReasoningEffort()
    {
        var provider = new ProviderRound((_, _) => Answer("Completed."))
        {
            BlockOnCall = 1,
        };
        await using var fixture = new RuntimeFixture(provider);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("First question."),
            CancellationToken.None).AsTask();
        await provider.BlockedCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var queued = await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp(
                "Second question.",
                AgentReasoningEffort.High),
            CancellationToken.None);

        Assert.True(queued.IsAccepted);
        Assert.Equal(1, fixture.Runtime.Snapshot.QueuedFollowUpCount);
        provider.ReleaseBlockedCall.TrySetResult();

        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, fixture.Runtime.Snapshot.QueuedFollowUpCount);
        Assert.Equal(2, provider.Requests.Count);
        var requests = provider.Requests.ToArray();
        Assert.Equal(AgentReasoningEffort.Automatic, requests[0].ReasoningEffort);
        Assert.Equal(AgentReasoningEffort.High, requests[1].ReasoningEffort);
        Assert.Contains(
            requests[1].Messages,
            message => message.Role == AgentMessageRole.User
                && string.Equals(message.Content, "Second question.", StringComparison.Ordinal));
        Assert.Equal(
            [
                "First question.",
                "Completed.",
                "Second question.",
                "Completed.",
            ],
            fixture.Runtime.Snapshot.Messages.Select(message => message.Content), StringComparer.Ordinal);
    }

    [Fact]
    public async Task FailedQueuedFollowUpIsReturnedForDraftRecovery()
    {
        var checkpoints = new InMemoryCheckpointStore();
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => Answer("Initial answer."),
            2 => [new AgentProviderEvent.ResponseStarted()],
            _ => throw new InvalidOperationException(
                "The provider received an unexpected request."),
        })
        {
            BlockOnCall = 1,
        };
        await using var fixture = new RuntimeFixture(
            provider,
            checkpointStore: checkpoints);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Initial question."),
            CancellationToken.None).AsTask();
        await provider.BlockedCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp(
                "Preserve this follow-up.",
                AgentReasoningEffort.High),
            CancellationToken.None);
        provider.ReleaseBlockedCall.TrySetResult();

        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(queued.IsAccepted);
        Assert.False(result.IsSuccess);
        Assert.True(result.InitialPromptCommitted);
        var recoverable = Assert.Single(result.RecoverableFollowUps!);
        Assert.Equal("Preserve this follow-up.", recoverable.Message);
        Assert.Equal(AgentReasoningEffort.High, recoverable.ReasoningEffort);
        Assert.Equal(GovernedAgentState.Failed, fixture.Runtime.Snapshot.State);
        Assert.Equal(0, fixture.Runtime.Snapshot.QueuedFollowUpCount);
        Assert.Equal(2, provider.Requests.Count);

        await using var restored = new RuntimeFixture(
            ProviderRound.AnswerEveryTurn(),
            checkpointStore: checkpoints);
        await restored.Runtime.RestoreLatestConversationAsync(
            CancellationToken.None);

        Assert.Equal(GovernedAgentState.Ready, restored.Runtime.Snapshot.State);
        Assert.Equal(
            [
                "Initial question.",
                "Initial answer.",
                "Preserve this follow-up.",
                "The previous agent turn was interrupted. No pending tool action was resumed.",
            ],
            restored.Runtime.Snapshot.Messages.Select(message => message.Content), StringComparer.Ordinal);
    }

    [Fact]
    public async Task FollowUpQueueIsBoundedAndStopDiscardsIt()
    {
        var provider = ProviderRound.Blocking();
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Wait."),
            CancellationToken.None).AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (var index = 0; index < 8; index++)
        {
            var queued = await fixture.Runtime.QueueFollowUpAsync(
                new GovernedAgentFollowUp($"Follow-up {index}."),
                CancellationToken.None);
            Assert.True(queued.IsAccepted);
        }

        var overflow = await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp("One too many."),
            CancellationToken.None);
        Assert.False(overflow.IsAccepted);
        Assert.Equal("agent_follow_up_queue_full", overflow.Code);
        Assert.Equal(8, fixture.Runtime.Snapshot.QueuedFollowUpCount);

        Assert.True((await fixture.Runtime.StopAsync(CancellationToken.None)).WasRunning);
        Assert.False((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        Assert.Equal(0, fixture.Runtime.Snapshot.QueuedFollowUpCount);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task QueuedMessagesCanBeEditedSortedPromotedAndDeleted()
    {
        var provider = ProviderRound.Blocking();
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Wait."),
            CancellationToken.None).AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var first = await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp("First."),
            CancellationToken.None);
        var second = await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp("Second."),
            CancellationToken.None);

        Assert.True(first.IsAccepted);
        Assert.True(second.IsAccepted);
        var firstId = Assert.IsType<AgentQueuedFollowUpId>(first.ItemId);
        var secondId = Assert.IsType<AgentQueuedFollowUpId>(second.ItemId);
        Assert.Equal(
            ["First.", "Second."],
            fixture.Runtime.Snapshot.QueuedFollowUps.Select(item => item.Message), StringComparer.Ordinal);

        Assert.True((await fixture.Runtime.UpdateQueuedFollowUpAsync(
            firstId,
            new GovernedAgentFollowUp("First edited.", AgentReasoningEffort.High),
            CancellationToken.None)).IsAccepted);
        Assert.True((await fixture.Runtime.MoveQueuedFollowUpAsync(
            secondId,
            0,
            CancellationToken.None)).IsAccepted);
        Assert.True((await fixture.Runtime.SteerQueuedFollowUpAsync(
            firstId,
            CancellationToken.None)).IsAccepted);

        Assert.Collection(
            fixture.Runtime.Snapshot.QueuedFollowUps,
            item =>
            {
                Assert.Equal(firstId, item.Id);
                Assert.Equal("First edited.", item.Message);
                Assert.Equal(AgentReasoningEffort.High, item.ReasoningEffort);
                Assert.Equal(GovernedAgentFollowUpDelivery.Steering, item.Delivery);
            },
            item => Assert.Equal(secondId, item.Id));

        Assert.True((await fixture.Runtime.RemoveQueuedFollowUpAsync(
            secondId,
            CancellationToken.None)).IsAccepted);
        Assert.Equal(firstId, Assert.Single(
            fixture.Runtime.Snapshot.QueuedFollowUps).Id);

        Assert.True((await fixture.Runtime.StopAsync(CancellationToken.None)).WasRunning);
        Assert.False((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task SteeringMessagesPreserveSubmissionOrderAheadOfOrdinaryFollowUps()
    {
        var provider = ProviderRound.Blocking();
        await using var fixture = new RuntimeFixture(provider);
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Wait."),
            CancellationToken.None).AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True((await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp("Ordinary."),
            CancellationToken.None)).IsAccepted);
        Assert.True((await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp(
                "Steer first.",
                delivery: GovernedAgentFollowUpDelivery.Steering),
            CancellationToken.None)).IsAccepted);
        Assert.True((await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp(
                "Steer second.",
                delivery: GovernedAgentFollowUpDelivery.Steering),
            CancellationToken.None)).IsAccepted);

        Assert.Equal(
            ["Steer first.", "Steer second.", "Ordinary."],
            fixture.Runtime.Snapshot.QueuedFollowUps.Select(item => item.Message), StringComparer.Ordinal);

        Assert.True((await fixture.Runtime.StopAsync(CancellationToken.None)).WasRunning);
        Assert.False((await sending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task SteeringWaitsForCompleteToolBatchThenPrecedesOrdinaryFollowUp()
    {
        var provider = new ProviderRound((call, _) => call switch
        {
            1 => ProviderRound.ToolBatch(
            [
                new ToolBatchCall(
                    "provider-read-first",
                    BuiltInAgentTools.TerminalReadScreen,
                    "{}"),
                new ToolBatchCall(
                    "provider-read-second",
                    BuiltInAgentTools.TerminalReadScreen,
                    "{}"),
            ]),
            2 => Answer("Answered after steering."),
            3 => Answer("Answered the follow-up."),
            _ => throw new InvalidOperationException(
                "The provider received an unexpected request."),
        })
        {
            BlockOnCall = 1,
        };
        await using var fixture = new RuntimeFixture(provider);
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(
                fixture.Context.Screen("first", contentRevision: 1)));
        fixture.Terminal.Results.Enqueue(
            new AgentTerminalActionResult.Screen(
                fixture.Context.Screen("second", contentRevision: 2)));
        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect first."),
            CancellationToken.None).AsTask();
        await provider.BlockedCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True((await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp("Ordinary follow-up."),
            CancellationToken.None)).IsAccepted);
        Assert.True((await fixture.Runtime.QueueFollowUpAsync(
            new GovernedAgentFollowUp(
                "Change direction now.",
                AgentReasoningEffort.High,
                GovernedAgentFollowUpDelivery.Steering),
            CancellationToken.None)).IsAccepted);
        provider.ReleaseBlockedCall.TrySetResult();

        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, provider.Requests.Count);
        var requests = provider.Requests.ToArray();
        Assert.Equal(
            [
                AgentMessageRole.System,
                AgentMessageRole.User,
                AgentMessageRole.Assistant,
                AgentMessageRole.Tool,
                AgentMessageRole.Tool,
                AgentMessageRole.User,
            ],
            requests[1].Messages.Select(message => message.Role));
        Assert.Equal(2, fixture.Terminal.Actions.Count);
        Assert.Equal("Change direction now.", requests[1].Messages[^1].Content);
        Assert.Equal(AgentReasoningEffort.High, requests[1].ReasoningEffort);
        Assert.Equal("Ordinary follow-up.", requests[2].Messages[^1].Content);
        Assert.Equal(
            [
                "Inspect first.",
                "Change direction now.",
                "Answered after steering.",
                "Ordinary follow-up.",
                "Answered the follow-up.",
            ],
            fixture.Runtime.Snapshot.Messages.Select(message => message.Content), StringComparer.Ordinal);
    }
}
