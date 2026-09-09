using System.Runtime.ExceptionServices;
using Asura.Application;

namespace Asura.Terminal;

internal static class TerminalAutomationWaiter
{
    private static readonly TimeSpan InitialPollInterval =
        TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan MaximumPollInterval =
        TimeSpan.FromMilliseconds(500);

    // The caller-selected timeout bounds only the condition (or explicit delay)
    // phase. Once that phase resolves, one final snapshot gets this separate,
    // hard cleanup budget so a faulty reader cannot hang the wait forever.
    private static readonly TimeSpan FinalReadCleanupTimeout =
        TimeSpan.FromSeconds(2);

    public static async ValueTask<TerminalWaitOutcome> WaitForDelayAsync(
        TerminalWaitForDelayInput input,
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(readScreen);
        ArgumentNullException.ThrowIfNull(readSession);
        var clock = timeProvider ?? TimeProvider.System;
        var started = clock.GetTimestamp();
        var initialRead = await ReadWithinDeadlineAsync(
                readScreen,
                started,
                input.Delay,
                cancellationToken,
                clock)
            .ConfigureAwait(false);
        if (initialRead.Status == WaitReadStatus.Cancelled)
        {
            return await CompleteWithFinalSnapshotAsync(
                    TerminalWaitOutcomeKind.Cancelled,
                    readScreen,
                    fallback: null,
                    initialContentRevision: null,
                    cancellationToken,
                    clock)
                .ConfigureAwait(false);
        }

        if (initialRead.Status == WaitReadStatus.Failed)
        {
            var sessionRead = await ReadWithinDeadlineAsync(
                    readSession,
                    started,
                    input.Delay,
                    cancellationToken,
                    clock)
                .ConfigureAwait(false);
            if (sessionRead.Status == WaitReadStatus.Cancelled)
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.Cancelled,
                        readScreen,
                        fallback: null,
                        initialContentRevision: null,
                        cancellationToken,
                        clock)
                    .ConfigureAwait(false);
            }

            if (sessionRead.Status == WaitReadStatus.Completed
                && SessionEnded(sessionRead.Value!))
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.SessionEnded,
                        readScreen,
                        fallback: null,
                        initialContentRevision: null,
                        cancellationToken,
                        clock)
                    .ConfigureAwait(false);
            }

            if (sessionRead.Status == WaitReadStatus.Failed)
            {
                ExceptionDispatchInfo.Capture(sessionRead.Error!).Throw();
            }

            if (sessionRead.Status != WaitReadStatus.DeadlineElapsed)
            {
                ExceptionDispatchInfo.Capture(initialRead.Error!).Throw();
            }
        }

        var initial = initialRead.Value;
        var initialContentRevision = initial?.ContentRevision;
        var remaining = Remaining(started, input.Delay, clock);
        if (remaining > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(remaining, clock, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.Cancelled,
                        readScreen,
                        initial,
                        initialContentRevision,
                        cancellationToken,
                        clock)
                    .ConfigureAwait(false);
            }
        }

        var finalRead = await TryReadFinalScreenAsync(
                readScreen,
                initial,
                cancellationToken,
                clock)
            .ConfigureAwait(false);
        if (finalRead.CallerCancelled)
        {
            return TerminalWaitOutcome.Cancelled(
                finalRead.Snapshot,
                initialContentRevision);
        }

        return finalRead.Snapshot is null
            ? TerminalWaitOutcome.Timeout(snapshot: null, initialContentRevision: null)
            : TerminalWaitOutcome.Elapsed(
                finalRead.Snapshot,
                initialContentRevision ?? finalRead.Snapshot.ContentRevision);
    }

    public static ValueTask<TerminalWaitOutcome> WaitForTextAsync(
        TerminalWaitForTextInput input,
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PollAsync(
            input.Timeout,
            initialContentRevision: null,
            readScreen,
            readSession,
            (snapshot, initialRevision, _) =>
                snapshot.PlainText.Contains(input.Text, StringComparison.Ordinal)
                    ? TerminalWaitOutcome.Matched(snapshot, initialRevision)
                    : null,
            cancellationToken,
            timeProvider ?? TimeProvider.System);
    }

    public static ValueTask<TerminalWaitOutcome> WaitForChangeAsync(
        TerminalWaitForChangeInput input,
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PollAsync(
            input.Timeout,
            input.AfterContentRevision,
            readScreen,
            readSession,
            (snapshot, initialRevision, _) =>
                snapshot.ContentRevision > input.AfterContentRevision
                    ? TerminalWaitOutcome.Changed(snapshot, initialRevision)
                    : null,
            cancellationToken,
            timeProvider ?? TimeProvider.System);
    }

    public static ValueTask<TerminalWaitOutcome> WaitForStableAsync(
        TerminalWaitForStableInput input,
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var clock = timeProvider ?? TimeProvider.System;
        long? lastRevision = null;
        var stableSince = 0L;
        return PollAsync(
            input.Timeout,
            initialContentRevision: null,
            readScreen,
            readSession,
            (snapshot, initialRevision, timestamp) =>
            {
                if (lastRevision != snapshot.ContentRevision)
                {
                    lastRevision = snapshot.ContentRevision;
                    stableSince = timestamp;
                    return null;
                }

                return clock.GetElapsedTime(stableSince, timestamp) >= input.StableFor
                    ? TerminalWaitOutcome.Stable(snapshot, initialRevision)
                    : null;
            },
            cancellationToken,
            clock);
    }

    public static ValueTask<TerminalWaitOutcome> WaitForPromptReadyAsync(
        TerminalWaitForPromptReadyInput input,
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PollAsync(
            input.Timeout,
            initialContentRevision: null,
            readScreen,
            readSession,
            (snapshot, initialRevision, _) =>
            {
                var shellEvent = FindShellEventAfter(
                    snapshot,
                    input.AfterShellEventSequence,
                    TerminalCommandBoundaryKind.CommandInputStarted);
                return shellEvent is null
                    ? null
                    : TerminalWaitOutcome.PromptReady(
                        snapshot,
                        initialRevision,
                        shellEvent);
            },
            cancellationToken,
            timeProvider ?? TimeProvider.System);
    }

    public static ValueTask<TerminalWaitOutcome> WaitForCommandFinishedAsync(
        TerminalWaitForCommandFinishedInput input,
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return PollAsync(
            input.Timeout,
            initialContentRevision: null,
            readScreen,
            readSession,
            (snapshot, initialRevision, _) =>
            {
                var shellEvent = FindShellEventAfter(
                    snapshot,
                    input.AfterShellEventSequence,
                    TerminalCommandBoundaryKind.CommandFinished);
                return shellEvent is null
                    ? null
                    : TerminalWaitOutcome.CommandFinished(
                        snapshot,
                        initialRevision,
                        shellEvent);
            },
            cancellationToken,
            timeProvider ?? TimeProvider.System);
    }

    private static TerminalShellIntegrationEvent? FindShellEventAfter(
        TerminalScreenSnapshot snapshot,
        long afterSequence,
        TerminalCommandBoundaryKind kind) =>
        snapshot.ShellIntegrationEvents.FirstOrDefault(shellEvent =>
            shellEvent.Sequence > afterSequence
            && shellEvent.Kind == kind);

    private static async ValueTask<TerminalWaitOutcome> PollAsync(
        TimeSpan timeout,
        long? initialContentRevision,
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        Func<CancellationToken, ValueTask<PanelSessionSnapshot>> readSession,
        Func<TerminalScreenSnapshot, long, long, TerminalWaitOutcome?> evaluate,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(readScreen);
        ArgumentNullException.ThrowIfNull(readSession);
        ArgumentNullException.ThrowIfNull(evaluate);
        var started = timeProvider.GetTimestamp();
        TerminalScreenSnapshot? lastSnapshot = null;
        var pollInterval = InitialPollInterval;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.Cancelled,
                        readScreen,
                        lastSnapshot,
                        initialContentRevision,
                        cancellationToken,
                        timeProvider)
                    .ConfigureAwait(false);
            }

            var screenRead = await ReadWithinDeadlineAsync(
                    readScreen,
                    started,
                    timeout,
                    cancellationToken,
                    timeProvider)
                .ConfigureAwait(false);
            if (screenRead.Status == WaitReadStatus.Cancelled)
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.Cancelled,
                        readScreen,
                        lastSnapshot,
                        initialContentRevision,
                        cancellationToken,
                        timeProvider)
                    .ConfigureAwait(false);
            }

            if (screenRead.Status == WaitReadStatus.DeadlineElapsed)
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.Timeout,
                        readScreen,
                        lastSnapshot,
                        initialContentRevision,
                        cancellationToken,
                        timeProvider)
                    .ConfigureAwait(false);
            }

            if (screenRead.Status == WaitReadStatus.Failed)
            {
                var sessionRead = await ReadWithinDeadlineAsync(
                        readSession,
                        started,
                        timeout,
                        cancellationToken,
                        timeProvider)
                    .ConfigureAwait(false);
                if (sessionRead.Status == WaitReadStatus.Cancelled)
                {
                    return await CompleteWithFinalSnapshotAsync(
                            TerminalWaitOutcomeKind.Cancelled,
                            readScreen,
                            lastSnapshot,
                            initialContentRevision,
                            cancellationToken,
                            timeProvider)
                        .ConfigureAwait(false);
                }

                if (sessionRead.Status == WaitReadStatus.DeadlineElapsed)
                {
                    return await CompleteWithFinalSnapshotAsync(
                            TerminalWaitOutcomeKind.Timeout,
                            readScreen,
                            lastSnapshot,
                            initialContentRevision,
                            cancellationToken,
                            timeProvider)
                        .ConfigureAwait(false);
                }

                if (sessionRead.Status == WaitReadStatus.Failed)
                {
                    ExceptionDispatchInfo.Capture(sessionRead.Error!).Throw();
                }

                if (SessionEnded(sessionRead.Value!))
                {
                    return await CompleteWithFinalSnapshotAsync(
                            TerminalWaitOutcomeKind.SessionEnded,
                            readScreen,
                            lastSnapshot,
                            initialContentRevision,
                            cancellationToken,
                            timeProvider)
                        .ConfigureAwait(false);
                }

                ExceptionDispatchInfo.Capture(screenRead.Error!).Throw();
            }

            var snapshot = screenRead.Value!;
            initialContentRevision ??= snapshot.ContentRevision;
            pollInterval = lastSnapshot is null
                || lastSnapshot.ContentRevision != snapshot.ContentRevision
                    ? InitialPollInterval
                    : NextPollInterval(pollInterval);
            lastSnapshot = snapshot;
            var now = timeProvider.GetTimestamp();
            var elapsed = timeProvider.GetElapsedTime(started, now);
            if (elapsed >= timeout)
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.Timeout,
                        readScreen,
                        lastSnapshot,
                        initialContentRevision,
                        cancellationToken,
                        timeProvider)
                    .ConfigureAwait(false);
            }

            if (evaluate(snapshot, initialContentRevision.Value, now) is { } completed)
            {
                return completed;
            }

            var sessionState = await ReadWithinDeadlineAsync(
                    readSession,
                    started,
                    timeout,
                    cancellationToken,
                    timeProvider)
                .ConfigureAwait(false);
            if (sessionState.Status == WaitReadStatus.Cancelled)
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.Cancelled,
                        readScreen,
                        lastSnapshot,
                        initialContentRevision,
                        cancellationToken,
                        timeProvider)
                    .ConfigureAwait(false);
            }

            if (sessionState.Status == WaitReadStatus.DeadlineElapsed)
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.Timeout,
                        readScreen,
                        lastSnapshot,
                        initialContentRevision,
                        cancellationToken,
                        timeProvider)
                    .ConfigureAwait(false);
            }

            if (sessionState.Status == WaitReadStatus.Failed)
            {
                ExceptionDispatchInfo.Capture(sessionState.Error!).Throw();
            }

            if (SessionEnded(sessionState.Value!))
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.SessionEnded,
                        readScreen,
                        snapshot,
                        initialContentRevision,
                        cancellationToken,
                        timeProvider)
                    .ConfigureAwait(false);
            }

            elapsed = timeProvider.GetElapsedTime(started);
            if (elapsed >= timeout)
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.Timeout,
                        readScreen,
                        lastSnapshot,
                        initialContentRevision,
                        cancellationToken,
                        timeProvider)
                    .ConfigureAwait(false);
            }

            var remaining = timeout - elapsed;
            var delay = remaining < pollInterval ? remaining : pollInterval;
            try
            {
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return await CompleteWithFinalSnapshotAsync(
                        TerminalWaitOutcomeKind.Cancelled,
                        readScreen,
                        lastSnapshot,
                        initialContentRevision,
                        cancellationToken,
                        timeProvider)
                    .ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan NextPollInterval(TimeSpan current)
    {
        var doubledTicks = current.Ticks > MaximumPollInterval.Ticks / 2
            ? MaximumPollInterval.Ticks
            : current.Ticks * 2;
        return TimeSpan.FromTicks(
            Math.Min(doubledTicks, MaximumPollInterval.Ticks));
    }

    private static bool SessionEnded(PanelSessionSnapshot snapshot) =>
        snapshot.Lifecycle is SessionLifecycle.Closed or SessionLifecycle.Failed;

    private static TimeSpan Remaining(
        long started,
        TimeSpan timeout,
        TimeProvider timeProvider)
    {
        var elapsed = timeProvider.GetElapsedTime(started);
        return elapsed >= timeout ? TimeSpan.Zero : timeout - elapsed;
    }

    private static async ValueTask<WaitReadResult<T>> ReadWithinDeadlineAsync<T>(
        Func<CancellationToken, ValueTask<T>> read,
        long started,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
        where T : class
    {
        var remaining = Remaining(started, timeout, timeProvider);
        if (remaining <= TimeSpan.Zero)
        {
            return new WaitReadResult<T>(
                WaitReadStatus.DeadlineElapsed,
                Value: null,
                Error: null);
        }

        using var deadline = new CancellationTokenSource(remaining, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            var value = await read(linked.Token)
                .AsTask()
                .WaitAsync(linked.Token)
                .ConfigureAwait(false);
            return new WaitReadResult<T>(WaitReadStatus.Completed, value, Error: null);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            return new WaitReadResult<T>(
                WaitReadStatus.Cancelled,
                Value: null,
                exception);
        }
        catch (Exception exception) when (deadline.IsCancellationRequested)
        {
            return new WaitReadResult<T>(
                WaitReadStatus.DeadlineElapsed,
                Value: null,
                exception);
        }
        catch (Exception exception)
        {
            return new WaitReadResult<T>(
                WaitReadStatus.Failed,
                Value: null,
                exception);
        }
    }

    private static async ValueTask<TerminalWaitOutcome>
        CompleteWithFinalSnapshotAsync(
            TerminalWaitOutcomeKind completionKind,
            Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
            TerminalScreenSnapshot? fallback,
            long? initialContentRevision,
            CancellationToken cancellationToken,
            TimeProvider timeProvider)
    {
        var finalRead = await TryReadFinalScreenAsync(
                readScreen,
                fallback,
                cancellationToken,
                timeProvider)
            .ConfigureAwait(false);
        if (finalRead.CallerCancelled)
        {
            completionKind = TerminalWaitOutcomeKind.Cancelled;
        }

        return completionKind switch
        {
            TerminalWaitOutcomeKind.Timeout => TerminalWaitOutcome.Timeout(
                finalRead.Snapshot,
                initialContentRevision),
            TerminalWaitOutcomeKind.Cancelled => TerminalWaitOutcome.Cancelled(
                finalRead.Snapshot,
                initialContentRevision),
            TerminalWaitOutcomeKind.SessionEnded => TerminalWaitOutcome.SessionEnded(
                finalRead.Snapshot,
                initialContentRevision),
            _ => throw new ArgumentOutOfRangeException(
                nameof(completionKind),
                completionKind,
                "Only non-successful wait completions require a cleanup snapshot."),
        };
    }

    private static async ValueTask<FinalScreenRead> TryReadFinalScreenAsync(
        Func<CancellationToken, ValueTask<TerminalScreenSnapshot>> readScreen,
        TerminalScreenSnapshot? fallback,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        using var cleanup = new CancellationTokenSource(
            FinalReadCleanupTimeout,
            timeProvider);
        if (!cancellationToken.IsCancellationRequested)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                cleanup.Token);
            try
            {
                var snapshot = await readScreen(linked.Token)
                    .AsTask()
                    .WaitAsync(linked.Token)
                    .ConfigureAwait(false);
                return new FinalScreenRead(
                    snapshot,
                    cancellationToken.IsCancellationRequested);
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                return new FinalScreenRead(fallback, CallerCancelled: false);
            }
        }

        // A caller cancellation may be the reason the first final read stopped.
        // Retry once without that cancelled token, but within the same cleanup
        // budget, so cancellation still returns the freshest obtainable state.
        try
        {
            var snapshot = await readScreen(cleanup.Token)
                .AsTask()
                .WaitAsync(cleanup.Token)
                .ConfigureAwait(false);
            return new FinalScreenRead(snapshot, CallerCancelled: true);
        }
        catch
        {
            return new FinalScreenRead(fallback, CallerCancelled: true);
        }
    }

    private enum WaitReadStatus
    {
        Completed,
        Cancelled,
        DeadlineElapsed,
        Failed,
    }

    private readonly record struct WaitReadResult<T>(
        WaitReadStatus Status,
        T? Value,
        Exception? Error)
        where T : class;

    private readonly record struct FinalScreenRead(
        TerminalScreenSnapshot? Snapshot,
        bool CallerCancelled);
}
