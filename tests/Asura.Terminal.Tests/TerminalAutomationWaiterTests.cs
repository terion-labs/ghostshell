using Asura.Application;

namespace Asura.Terminal.Tests;

public sealed class TerminalAutomationWaiterTests
{
    [Fact]
    public async Task CancellationPerformsOneFreshBoundedFinalRead()
    {
        using var cancellation = new CancellationTokenSource();
        var firstRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;

        ValueTask<TerminalScreenSnapshot> ReadScreen(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = Interlocked.Increment(ref readCount);
            firstRead.TrySetResult();
            return ValueTask.FromResult(Screen(revision));
        }

        var waiting = TerminalAutomationWaiter.WaitForTextAsync(
            new TerminalWaitForTextInput(
                "never",
                TimeSpan.FromSeconds(30)),
            ReadScreen,
            ReadHealthySession,
            cancellation.Token).AsTask();
        await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await cancellation.CancelAsync();
        var outcome = await waiting.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TerminalWaitOutcomeKind.Cancelled, outcome.Kind);
        Assert.NotNull(outcome.Snapshot);
        Assert.True(readCount >= 2);
        Assert.Equal(readCount, outcome.Snapshot.ContentRevision);
        Assert.Equal(1, outcome.InitialContentRevision);
    }

    [Fact]
    public async Task ConditionReadCannotOutliveRequestedTimeoutAndGetsCleanupSnapshot()
    {
        var readCount = 0;

        async ValueTask<TerminalScreenSnapshot> ReadScreen(
            CancellationToken cancellationToken)
        {
            var revision = Interlocked.Increment(ref readCount);
            if (revision == 2)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Screen(revision);
        }

        var outcome = await TerminalAutomationWaiter.WaitForTextAsync(
                new TerminalWaitForTextInput(
                    "never",
                    TimeSpan.FromMilliseconds(250)),
                ReadScreen,
                ReadHealthySession,
                CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TerminalWaitOutcomeKind.Timeout, outcome.Kind);
        Assert.Equal(3, readCount);
        Assert.Equal(3, outcome.Snapshot?.ContentRevision);
        Assert.Equal(1, outcome.InitialContentRevision);
    }

    [Fact]
    public async Task SessionFallbackReadCannotOutliveRequestedTimeout()
    {
        var sessionReadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask<TerminalScreenSnapshot> ReadScreen(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromException<TerminalScreenSnapshot>(
                new IOException("screen unavailable"));
        }

        async ValueTask<PanelSessionSnapshot> ReadSession(
            CancellationToken cancellationToken)
        {
            Assert.True(cancellationToken.CanBeCanceled);
            sessionReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        var waiting = TerminalAutomationWaiter.WaitForTextAsync(
            new TerminalWaitForTextInput(
                "never",
                TimeSpan.FromMilliseconds(150)),
            ReadScreen,
            ReadSession,
            CancellationToken.None).AsTask();
        await sessionReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var outcome = await waiting.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TerminalWaitOutcomeKind.Timeout, outcome.Kind);
        Assert.Null(outcome.Snapshot);
    }

    [Fact]
    public async Task CancellationDuringDelayFinalReadRetriesWithinCleanupBudget()
    {
        using var cancellation = new CancellationTokenSource();
        var finalReadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;

        async ValueTask<TerminalScreenSnapshot> ReadScreen(
            CancellationToken cancellationToken)
        {
            var revision = Interlocked.Increment(ref readCount);
            if (revision == 2)
            {
                finalReadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return Screen(revision);
        }

        // The requested delay is the condition deadline. The mandatory final
        // snapshot begins afterward and has its own hard two-second budget.
        var waiting = TerminalAutomationWaiter.WaitForDelayAsync(
            new TerminalWaitForDelayInput(TimeSpan.FromMilliseconds(50)),
            ReadScreen,
            ReadHealthySession,
            cancellation.Token).AsTask();
        await finalReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();

        var outcome = await waiting.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(TerminalWaitOutcomeKind.Cancelled, outcome.Kind);
        Assert.Equal(3, readCount);
        Assert.Equal(3, outcome.Snapshot?.ContentRevision);
        Assert.Equal(1, outcome.InitialContentRevision);
    }

    [Fact]
    public async Task UnchangedLongWaitUsesBoundedAdaptivePolling()
    {
        var clock = new ManualTimerTimeProvider();
        var readCount = 0;
        var waiting = TerminalAutomationWaiter.WaitForTextAsync(
            new TerminalWaitForTextInput(
                "never",
                TimeSpan.FromMilliseconds(1_200)),
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Increment(ref readCount);
                return ValueTask.FromResult(Screen(0));
            },
            ReadHealthySession,
            CancellationToken.None,
            clock).AsTask();
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        TimeSpan[] expectedDelays =
        [
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(160),
            TimeSpan.FromMilliseconds(320),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(80),
        ];
        foreach (var delay in expectedDelays)
        {
            await clock.WaitForActiveTimerAsync(delay, testTimeout.Token);
            clock.Advance(delay);
        }

        var outcome = await waiting.WaitAsync(testTimeout.Token);

        Assert.Equal(TerminalWaitOutcomeKind.Timeout, outcome.Kind);
        Assert.Equal(8, readCount);
    }

    private static ValueTask<PanelSessionSnapshot> ReadHealthySession(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PanelSessionSnapshot(
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            HasActiveWork: false,
            StatusDetail: "ready"));
    }

    private static TerminalScreenSnapshot Screen(long contentRevision) =>
        new(
            $"screen {contentRevision}",
            CursorRow: 0,
            CursorColumn: 0,
            Rows: 24,
            Columns: 80,
            IsAlternateScreen: false,
            WorkingDirectory: null,
            CapturedAtUtc: DateTimeOffset.UnixEpoch,
            ContentRevision: contentRevision);
}
