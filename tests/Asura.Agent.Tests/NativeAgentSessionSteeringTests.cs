using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Asura.Agent;

namespace Asura.Agent.Tests;

public sealed partial class NativeAgentSessionTests
{
    [Fact]
    public async Task SteeringReplacesTheInitialGenerationInsideItsOriginalTurn()
    {
        var session = CreateSession();
        var provider = new SteerableProvider(holdReplacement: true);
        var turn = session.RunTurnAsync(
            "Investigate disk",
            [],
            provider,
            CancellationToken.None).AsTask();
        await provider.OldPaused.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var steer = session.Steer(1, "Focus on journal errors");

        Assert.True(steer.Succeeded);
        Assert.Equal(2, steer.ReplacementGeneration);
        Assert.Equal(
            "Investigate disk\n\nSteering update:\nFocus on journal errors",
            steer.ReplacementUserMessage);
        Assert.True(steer.ContainsUntrustedContent);
        Assert.Equal(NativeAgentSessionState.Streaming, session.Snapshot().State);
        Assert.Equal(2, session.Snapshot().Generation);
        Assert.True(provider.OldToken.IsCancellationRequested);

        await provider.ReplacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        provider.ReleaseReplacement.TrySetResult();
        var result = await turn.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(result.Succeeded);
        Assert.Equal(AgentProviderStopReason.EndTurn, result.StopReason);

        provider.ReleaseOld.TrySetResult();
        await provider.OldFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var requests = provider.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(1, requests[0].Generation);
        Assert.Equal("Investigate disk", Assert.Single(requests[0].Messages).Content);
        Assert.Equal(2, requests[1].Generation);
        Assert.Equal(
            steer.ReplacementUserMessage,
            Assert.Single(requests[1].Messages).Content);

        var snapshot = session.Snapshot();
        Assert.Equal(NativeAgentSessionState.Ready, snapshot.State);
        Assert.Collection(
            snapshot.Conversation,
            message =>
            {
                Assert.Equal(AgentMessageRole.User, message.Role);
                Assert.Equal(steer.ReplacementUserMessage, message.Content);
            },
            message =>
            {
                Assert.Equal(AgentMessageRole.Assistant, message.Role);
                Assert.Equal("replacement response", message.Content);
            });

        var events = await ReadCurrentEventBatchAsync(session);
        Assert.Equal(
            [
                AgentRunEventKind.TurnStarted,
                AgentRunEventKind.ProvisionalText,
                AgentRunEventKind.TurnSteered,
                AgentRunEventKind.TurnStarted,
                AgentRunEventKind.ProvisionalText,
                AgentRunEventKind.TurnCommitted,
            ],
            events.Select(agentEvent => agentEvent.Kind));
        var steeredEvent = Assert.Single(
            events,
            agentEvent => agentEvent.Kind == AgentRunEventKind.TurnSteered);
        Assert.Equal(1, steeredEvent.Generation);
        Assert.False(steeredEvent.ContainsUntrustedContent);
        Assert.Null(steeredEvent.ProvisionalText);
        Assert.DoesNotContain(
            events,
            agentEvent => string.Equals(agentEvent.ProvisionalText, "late old response", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReplacementGenerationCannotBeSteeredAgain()
    {
        var session = CreateSession();
        var provider = new SteerableProvider(holdReplacement: true);
        var turn = session.RunTurnAsync(
            "Initial request",
            [],
            provider,
            CancellationToken.None).AsTask();
        await provider.OldPaused.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var accepted = session.Steer(1, "First and only update");
        await provider.ReplacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var staleRetry = session.Steer(1, "Retry old generation");
        var secondUpdate = session.Steer(
            accepted.ReplacementGeneration!.Value,
            "Second update");

        Assert.Equal(
            AgentSteerErrorCode.GenerationMismatch,
            staleRetry.ErrorCode);
        Assert.Equal(
            AgentSteerErrorCode.AlreadySteered,
            secondUpdate.ErrorCode);
        Assert.False(staleRetry.ContainsUntrustedContent);
        Assert.False(secondUpdate.ContainsUntrustedContent);

        provider.ReleaseReplacement.TrySetResult();
        Assert.True((await turn.WaitAsync(TimeSpan.FromSeconds(1))).Succeeded);
        provider.ReleaseOld.TrySetResult();
        await provider.OldFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ToolResultContinuationIsNotSteerable()
    {
        var session = CreateSession();
        var tools = System.Collections.Immutable.ImmutableArray.Create(
            Tool("terminal.read_screen"));
        var proposalTurn = await session.RunTurnAsync(
            "Inspect",
            tools,
            ToolProvider("terminal.read_screen", "{}"),
            CancellationToken.None);
        var proposal = Assert.Single(proposalTurn.ToolProposals);
        var continuationProvider = new NonCooperativeProvider();
        var continuation = session.SubmitToolResultsAsync(
            proposal.Generation,
            [SuccessJson(proposal, "{}")],
            tools,
            continuationProvider,
            CancellationToken.None).AsTask();
        await continuationProvider.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var steer = session.Steer(2, "Change course");

        Assert.False(steer.Succeeded);
        Assert.Equal(AgentSteerErrorCode.NotInitialUserTurn, steer.ErrorCode);
        Assert.Equal(2, session.Snapshot().Generation);

        continuationProvider.Release.TrySetResult();
        Assert.True(
            (await continuation.WaitAsync(TimeSpan.FromSeconds(1))).Succeeded);
    }

    [Fact]
    public async Task SteeringReservesCapacityForANonCooperativeOldProvider()
    {
        var limits = new AgentKernelLimits(maximumConcurrentProviderOperations: 1);
        var session = CreateSession(limits: limits);
        var provider = new SteerableProvider();
        var turn = session.RunTurnAsync(
            "Initial request",
            [],
            provider,
            CancellationToken.None).AsTask();
        await provider.OldPaused.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var steer = session.Steer(1, "Replacement request");

        Assert.False(steer.Succeeded);
        Assert.Equal(
            AgentSteerErrorCode.ProviderOperationLimit,
            steer.ErrorCode);
        Assert.Equal(1, session.Snapshot().Generation);
        Assert.False(provider.OldToken.IsCancellationRequested);
        Assert.DoesNotContain(
            provider.Requests,
            request => request.Generation == 2);

        provider.ReleaseOld.TrySetResult();
        Assert.True((await turn.WaitAsync(TimeSpan.FromSeconds(1))).Succeeded);
        await provider.OldFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task OversizedSteeringUpdateChangesNothing()
    {
        var session = CreateSession();
        var provider = new SteerableProvider();
        var turn = session.RunTurnAsync(
            "Initial request",
            [],
            provider,
            CancellationToken.None).AsTask();
        await provider.OldPaused.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var steer = session.Steer(
            1,
            new string('x', AgentKernelLimits.Default.MaximumAssistantTextBytes + 1));

        Assert.False(steer.Succeeded);
        Assert.Equal(AgentSteerErrorCode.LimitExceeded, steer.ErrorCode);
        Assert.Equal(1, session.Snapshot().Generation);
        Assert.False(provider.OldToken.IsCancellationRequested);

        provider.ReleaseOld.TrySetResult();
        Assert.True((await turn.WaitAsync(TimeSpan.FromSeconds(1))).Succeeded);
        await provider.OldFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task MultibyteReplacementIsBoundedByItsCombinedUtf8Bytes()
    {
        var session = CreateSession();
        var provider = new SteerableProvider();
        var turn = session.RunTurnAsync(
            "Initial request",
            [],
            provider,
            CancellationToken.None).AsTask();
        await provider.OldPaused.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var individuallyBoundedUpdate = new string(
            '界',
            AgentKernelLimits.Default.MaximumAssistantTextBytes / 3);

        var steer = session.Steer(1, individuallyBoundedUpdate);

        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(individuallyBoundedUpdate)
            <= AgentKernelLimits.Default.MaximumAssistantTextBytes);
        Assert.False(steer.Succeeded);
        Assert.Equal(AgentSteerErrorCode.LimitExceeded, steer.ErrorCode);
        Assert.Equal(1, session.Snapshot().Generation);
        Assert.False(provider.OldToken.IsCancellationRequested);

        provider.ReleaseOld.TrySetResult();
        Assert.True((await turn.WaitAsync(TimeSpan.FromSeconds(1))).Succeeded);
        await provider.OldFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CancelAfterSteeringFencesBothProviderGenerations()
    {
        var session = CreateSession();
        var provider = new SteerableProvider(holdReplacement: true);
        var turn = session.RunTurnAsync(
            "Initial request",
            [],
            provider,
            CancellationToken.None).AsTask();
        await provider.OldPaused.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(session.Steer(1, "Replacement request").Succeeded);
        await provider.ReplacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(session.Cancel());
        var result = await turn.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(AgentTurnErrorCode.Cancelled, result.ErrorCode);
        Assert.True(provider.OldToken.IsCancellationRequested);
        Assert.True(provider.ReplacementToken.IsCancellationRequested);
        Assert.Equal(NativeAgentSessionState.Cancelled, session.Snapshot().State);
        Assert.Empty(session.Snapshot().Conversation);

        provider.ReleaseOld.TrySetResult();
        provider.ReleaseReplacement.TrySetResult();
        await provider.OldFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await provider.ReplacementFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var events = await ReadCurrentEventBatchAsync(session);
        Assert.DoesNotContain(
            events,
            agentEvent => agentEvent.ProvisionalText is
                "late old response" or "replacement response");
        Assert.DoesNotContain(
            events,
            agentEvent => agentEvent.Kind == AgentRunEventKind.TurnCommitted);
    }

    [Fact]
    public async Task CallerCancellationAfterSteeringFencesBothProviderGenerations()
    {
        var session = CreateSession();
        var provider = new SteerableProvider(holdReplacement: true);
        using var cancellation = new CancellationTokenSource();
        var turn = session.RunTurnAsync(
            "Initial request",
            [],
            provider,
            cancellation.Token).AsTask();
        await provider.OldPaused.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(session.Steer(1, "Replacement request").Succeeded);
        await provider.ReplacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var cancellationException = Record.Exception(cancellation.Cancel);
        var result = await turn.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Null(cancellationException);
        Assert.Equal(AgentTurnErrorCode.Cancelled, result.ErrorCode);
        Assert.True(provider.OldToken.IsCancellationRequested);
        Assert.True(provider.ReplacementToken.IsCancellationRequested);
        Assert.Equal(NativeAgentSessionState.Cancelled, session.Snapshot().State);
        Assert.Empty(session.Snapshot().Conversation);

        provider.ReleaseOld.TrySetResult();
        provider.ReleaseReplacement.TrySetResult();
        await provider.OldFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await provider.ReplacementFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var events = await ReadCurrentEventBatchAsync(session);
        Assert.DoesNotContain(
            events,
            agentEvent => agentEvent.ProvisionalText is
                "late old response" or "replacement response");
        Assert.DoesNotContain(
            events,
            agentEvent => agentEvent.Kind == AgentRunEventKind.TurnCommitted);
    }

    [Fact]
    public async Task CommitAndSteerRaceHasOneConsistentWinner()
    {
        for (var iteration = 0; iteration < 24; iteration++)
        {
            var session = CreateSession();
            var provider = new SteerableProvider();
            var turn = session.RunTurnAsync(
                "Initial request",
                [],
                provider,
                CancellationToken.None).AsTask();
            await provider.OldPaused.Task.WaitAsync(TimeSpan.FromSeconds(1));
            using var start = new Barrier(3);
            var steerTask = Task.Run(
                () =>
                {
                    start.SignalAndWait();
                    return session.Steer(1, "Replacement request");
                });
            var releaseTask = Task.Run(
                () =>
                {
                    start.SignalAndWait();
                    provider.ReleaseOld.TrySetResult();
                });
            start.SignalAndWait();

            var steer = await steerTask.WaitAsync(TimeSpan.FromSeconds(1));
            await releaseTask.WaitAsync(TimeSpan.FromSeconds(1));
            var result = await turn.WaitAsync(TimeSpan.FromSeconds(1));
            await provider.OldFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(result.Succeeded);
            var conversation = session.Snapshot().Conversation;
            Assert.Equal(2, conversation.Length);
            if (steer.Succeeded)
            {
                Assert.Equal(2, session.Snapshot().Generation);
                Assert.Equal(steer.ReplacementUserMessage, conversation[0].Content);
                Assert.Equal("replacement response", conversation[1].Content);
            }
            else
            {
                Assert.Equal(AgentSteerErrorCode.NoActiveTurn, steer.ErrorCode);
                Assert.Equal(1, session.Snapshot().Generation);
                Assert.Equal("Initial request", conversation[0].Content);
                Assert.Equal("obsoletelate old response", conversation[1].Content);
            }
        }
    }

    [Fact]
    public async Task GenerationMismatchDoesNotAffectTheActiveTurn()
    {
        var session = CreateSession();
        var noTurn = session.Steer(1, "No turn");
        Assert.Equal(AgentSteerErrorCode.NoActiveTurn, noTurn.ErrorCode);

        var provider = new SteerableProvider();
        var turn = session.RunTurnAsync(
            "Initial request",
            [],
            provider,
            CancellationToken.None).AsTask();
        await provider.OldPaused.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var mismatched = session.Steer(2, "Wrong generation");

        Assert.Equal(
            AgentSteerErrorCode.GenerationMismatch,
            mismatched.ErrorCode);
        Assert.Equal(1, session.Snapshot().Generation);
        Assert.False(provider.OldToken.IsCancellationRequested);

        provider.ReleaseOld.TrySetResult();
        Assert.True((await turn.WaitAsync(TimeSpan.FromSeconds(1))).Succeeded);
        await provider.OldFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private sealed class SteerableProvider(bool holdReplacement = false) : IAgentProvider
    {
        private int _callCount;

        public ConcurrentQueue<AgentProviderRequest> Requests { get; } = new();

        public TaskCompletionSource OldPaused { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseOld { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource OldFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReplacementStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseReplacement { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReplacementFinished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken OldToken { get; private set; }

        public CancellationToken ReplacementToken { get; private set; }

        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                OldToken = cancellationToken;
                try
                {
                    yield return new AgentProviderEvent.ResponseStarted();
                    yield return new AgentProviderEvent.TextDelta("obsolete");
                    OldPaused.TrySetResult();
                    await ReleaseOld.Task.ConfigureAwait(false);
                    yield return new AgentProviderEvent.TextDelta("late old response");
                    yield return new AgentProviderEvent.ResponseCompleted(
                        AgentProviderStopReason.EndTurn);
                }
                finally
                {
                    OldFinished.TrySetResult();
                }

                yield break;
            }

            if (call != 2)
            {
                throw new InvalidOperationException(
                    "A steered turn may invoke the provider at most twice.");
            }

            ReplacementToken = cancellationToken;
            ReplacementStarted.TrySetResult();
            try
            {
                yield return new AgentProviderEvent.ResponseStarted();
                if (holdReplacement)
                {
                    await ReleaseReplacement.Task.ConfigureAwait(false);
                }

                yield return new AgentProviderEvent.TextDelta("replacement response");
                yield return new AgentProviderEvent.ResponseCompleted(
                    AgentProviderStopReason.EndTurn);
            }
            finally
            {
                ReplacementFinished.TrySetResult();
            }
        }
    }
}
