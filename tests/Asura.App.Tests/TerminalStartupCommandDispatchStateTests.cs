using Asura.App;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class TerminalStartupCommandDispatchStateTests
{
    [Fact]
    public async Task RetryReusesTheExactBatchRequestAndIdempotencyIdentity()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var state = CreateState(timeProvider);
        var contexts = new List<OperationContext>();
        var attempts = 0;

        async ValueTask<TerminalStartupCommandDispatchResult> Dispatch(
            OperationContext context,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            contexts.Add(context);
            attempts++;
            return attempts == 1
                ? TerminalStartupCommandDispatchResult.Failure(
                    new TerminalStartupCommandDispatchError(
                        TerminalStartupCommandDispatchErrorCode.WriteOutcomeUnknown,
                        "Acknowledgement lost.",
                        Retryable: true))
                : TerminalStartupCommandDispatchResult.Success();
        }

        var failed = await state.DispatchIfNeededAsync(
            state.PanelId,
            Dispatch,
            CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var succeeded = await state.DispatchIfNeededAsync(
            state.PanelId,
            Dispatch,
            CancellationToken.None);
        var suppressed = await state.DispatchIfNeededAsync(
            state.PanelId,
            Dispatch,
            CancellationToken.None);

        Assert.False(failed!.CommandsDelivered);
        Assert.True(succeeded!.Succeeded);
        Assert.Null(suppressed);
        Assert.Equal(2, contexts.Count);
        Assert.NotNull(contexts[0].IdempotencyKey);
        Assert.Same(contexts[0], contexts[1]);
        Assert.Equal(contexts[0].RequestId, contexts[1].RequestId);
        Assert.Equal(contexts[0].IdempotencyKey, contexts[1].IdempotencyKey);
    }

    [Fact]
    public async Task ConcurrentLiveCallbacksProduceOnlyOneSuccessfulDispatch()
    {
        var state = CreateState();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var dispatchCount = 0;

        async ValueTask<TerminalStartupCommandDispatchResult> Dispatch(
            OperationContext batchContext,
            CancellationToken cancellationToken)
        {
            _ = batchContext;
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            Interlocked.Increment(ref dispatchCount);
            entered.TrySetResult();
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return TerminalStartupCommandDispatchResult.Success();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        var first = state.DispatchIfNeededAsync(
            state.PanelId,
            Dispatch,
            CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = state.DispatchIfNeededAsync(
            state.PanelId,
            Dispatch,
            CancellationToken.None).AsTask();
        Assert.False(duplicate.IsCompleted);

        release.TrySetResult();
        var results = await Task.WhenAll(first, duplicate);

        Assert.Equal(1, maximumActive);
        Assert.Equal(1, dispatchCount);
        Assert.True(results[0]!.Succeeded);
        Assert.Null(results[1]);
    }

    [Fact]
    public async Task RetryableFailuresUseOneTwoThenFiveSecondCappedBackoff()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var state = CreateState(timeProvider);
        var dispatchCount = 0;

        ValueTask<TerminalStartupCommandDispatchResult> Dispatch(
            OperationContext batchContext,
            CancellationToken cancellationToken)
        {
            _ = batchContext;
            cancellationToken.ThrowIfCancellationRequested();
            dispatchCount++;
            return ValueTask.FromResult(TerminalStartupCommandDispatchResult.Failure(
                new TerminalStartupCommandDispatchError(
                    TerminalStartupCommandDispatchErrorCode.WriteOutcomeUnknown,
                    "Acknowledgement lost.",
                    Retryable: true)));
        }

        Assert.NotNull(await state.DispatchIfNeededAsync(
            state.PanelId,
            Dispatch,
            CancellationToken.None));
        Assert.Equal(1, dispatchCount);

        await AssertSuppressedUntilAsync(TimeSpan.FromSeconds(1));
        await AssertSuppressedUntilAsync(TimeSpan.FromSeconds(2));
        await AssertSuppressedUntilAsync(TimeSpan.FromSeconds(5));
        await AssertSuppressedUntilAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, dispatchCount);

        async Task AssertSuppressedUntilAsync(TimeSpan delay)
        {
            timeProvider.Advance(delay - TimeSpan.FromMilliseconds(1));
            Assert.Null(await state.DispatchIfNeededAsync(
                state.PanelId,
                Dispatch,
                CancellationToken.None));
            timeProvider.Advance(TimeSpan.FromMilliseconds(1));
            Assert.NotNull(await state.DispatchIfNeededAsync(
                state.PanelId,
                Dispatch,
                CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopPolicyMakesTheFirstTypedDeliveryFailureTerminal(bool retryable)
    {
        var state = CreateState(
            failurePolicy:
                StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);
        var dispatchCount = 0;

        ValueTask<TerminalStartupCommandDispatchResult> Dispatch(
            OperationContext batchContext,
            CancellationToken cancellationToken)
        {
            _ = batchContext;
            cancellationToken.ThrowIfCancellationRequested();
            dispatchCount++;
            return ValueTask.FromResult(TerminalStartupCommandDispatchResult.Failure(
                new TerminalStartupCommandDispatchError(
                    TerminalStartupCommandDispatchErrorCode.WriteOutcomeUnknown,
                    "Acknowledgement lost.",
                    retryable)));
        }

        Assert.NotNull(await state.DispatchIfNeededAsync(
            state.PanelId,
            Dispatch,
            CancellationToken.None));
        Assert.Null(await state.DispatchIfNeededAsync(
            state.PanelId,
            Dispatch,
            CancellationToken.None));
        Assert.Equal(1, dispatchCount);
    }

    [Fact]
    public async Task ConfirmedDeliveryWithCompletionAuditUncertaintyNeverReplays()
    {
        var state = CreateState();
        var dispatchCount = 0;

        ValueTask<TerminalStartupCommandDispatchResult> Dispatch(
            OperationContext batchContext,
            CancellationToken cancellationToken)
        {
            _ = batchContext;
            cancellationToken.ThrowIfCancellationRequested();
            dispatchCount++;
            return ValueTask.FromResult(TerminalStartupCommandDispatchResult.Failure(
                new TerminalStartupCommandDispatchError(
                    TerminalStartupCommandDispatchErrorCode.AuditPersistenceFailure,
                    "The completion audit outcome is uncertain.",
                    Retryable: true),
                commandsDelivered: true));
        }

        Assert.NotNull(await state.DispatchIfNeededAsync(
            state.PanelId,
            Dispatch,
            CancellationToken.None));
        Assert.Null(await state.DispatchIfNeededAsync(
            state.PanelId,
            Dispatch,
            CancellationToken.None));
        Assert.Equal(1, dispatchCount);
    }

    [Fact]
    public async Task StopPolicySerializesRendererReplacementBehindTheCancelledFirstAttempt()
    {
        var state = CreateState(
            failurePolicy:
                StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);
        using var firstLifetime = new CancellationTokenSource();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;

        async ValueTask<TerminalStartupCommandDispatchResult> FirstDispatch(
            OperationContext batchContext,
            CancellationToken cancellationToken)
        {
            _ = batchContext;
            dispatchCount++;
            firstEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The cancelled dispatch unexpectedly resumed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return TerminalStartupCommandDispatchResult.Failure(
                    new TerminalStartupCommandDispatchError(
                        TerminalStartupCommandDispatchErrorCode.Cancelled,
                        "The renderer attachment changed.",
                        Retryable: true));
            }
        }

        var first = state.DispatchIfNeededAsync(
            state.PanelId,
            FirstDispatch,
            firstLifetime.Token).AsTask();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var replacement = state.DispatchIfNeededAsync(
            state.PanelId,
            (_, _) =>
            {
                dispatchCount++;
                return ValueTask.FromResult(
                    TerminalStartupCommandDispatchResult.Success());
            },
            CancellationToken.None).AsTask();

        Assert.False(replacement.IsCompleted);
        firstLifetime.Cancel();

        var firstResult = await first;
        var replacementResult = await replacement;

        Assert.Equal(
            TerminalStartupCommandDispatchErrorCode.Cancelled,
            firstResult!.Error?.Code);
        Assert.Null(replacementResult);
        Assert.Equal(1, dispatchCount);
    }

    [Fact]
    public async Task WrongPanelCannotDispatchOrMutateTheOwnedBatch()
    {
        var state = CreateState();
        var dispatchCount = 0;

        var wrongPanelResult = await state.DispatchIfNeededAsync(
            PanelInstanceId.New(),
            (_, _) =>
            {
                dispatchCount++;
                return ValueTask.FromResult(
                    TerminalStartupCommandDispatchResult.Success());
            },
            CancellationToken.None);

        Assert.Null(wrongPanelResult);
        Assert.Null(state.LastResult);
        Assert.Equal(0, dispatchCount);

        var correctPanelResult = await state.DispatchIfNeededAsync(
            state.PanelId,
            (_, _) =>
            {
                dispatchCount++;
                return ValueTask.FromResult(
                    TerminalStartupCommandDispatchResult.Success());
            },
            CancellationToken.None);

        Assert.True(correctPanelResult!.Succeeded);
        Assert.Same(correctPanelResult, state.LastResult);
        Assert.Equal(1, dispatchCount);
    }

    [Fact]
    public void CommandsAreAnImmutableCopyOfTheDefinitionInstanceBatch()
    {
        var commands = new List<string> { "deploy", "status\n" };
        var context = OperationContext.ForHuman(
            new ClientId("startup-client"),
            idempotencyKey: IdempotencyKey.New());
        var state = new TerminalStartupCommandDispatchState(
            PanelInstanceId.New(),
            commands,
            context);

        commands[0] = "dangerous replacement";
        commands.Add("unexpected");

        Assert.Equal(["deploy", "status"], state.Commands);
        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)state.Commands).Add("mutate"));
    }

    [Fact]
    public async Task TypedOutcomeIsStoredAndPublishedBeforeTheHostObservesIt()
    {
        var state = CreateState();
        TerminalStartupCommandDispatchEventArgs? observed = null;
        state.DispatchCompleted += (_, eventArgs) =>
        {
            Assert.Same(eventArgs.Result, state.LastResult);
            observed = eventArgs;
        };

        var result = await state.DispatchIfNeededAsync(
            state.PanelId,
            (_, _) => ValueTask.FromResult(
                TerminalStartupCommandDispatchResult.Failure(
                    new TerminalStartupCommandDispatchError(
                        TerminalStartupCommandDispatchErrorCode.WriteOutcomeUnknown,
                        "Acknowledgement lost.",
                        Retryable: true))),
            CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Same(state.Context, observed.Context);
        Assert.Same(result, observed.Result);
    }

    [Fact]
    public async Task DisposingTheRuntimeStateCancelsPendingDeliveryAndFutureDispatch()
    {
        var state = CreateState();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchCount = 0;

        var pending = state.DispatchIfNeededAsync(
            state.PanelId,
            async (_, cancellationToken) =>
            {
                dispatchCount++;
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException(
                        "The disposed startup batch unexpectedly resumed.");
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    return TerminalStartupCommandDispatchResult.Failure(
                        new TerminalStartupCommandDispatchError(
                            TerminalStartupCommandDispatchErrorCode.Cancelled,
                            "The runtime panel closed.",
                            Retryable: true));
                }
            },
            CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        state.Dispose();

        var cancelled = await pending;
        var suppressed = await state.DispatchIfNeededAsync(
            state.PanelId,
            (_, _) =>
            {
                dispatchCount++;
                return ValueTask.FromResult(
                    TerminalStartupCommandDispatchResult.Success());
            },
            CancellationToken.None);

        Assert.Equal(
            TerminalStartupCommandDispatchErrorCode.Cancelled,
            cancelled!.Error?.Code);
        Assert.Null(suppressed);
        Assert.Equal(1, dispatchCount);
    }

    private static TerminalStartupCommandDispatchState CreateState(
        TimeProvider? timeProvider = null,
        StartupCommandDeliveryFailurePolicy failurePolicy =
            StartupCommandDeliveryFailurePolicy.RetryWhileLive)
    {
        var context = OperationContext.ForHuman(
            new ClientId("startup-client"),
            idempotencyKey: IdempotencyKey.New());
        return new TerminalStartupCommandDispatchState(
            PanelInstanceId.New(),
            ["deploy"],
            context,
            timeProvider,
            failurePolicy);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
