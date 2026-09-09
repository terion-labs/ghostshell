using System.Runtime.InteropServices;
using Asura.Application;

namespace Asura.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    private static readonly TimeSpan BrowserWaitPollInterval =
        TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan BrowserWaitMaximumPollInterval =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BrowserWaitFinalReadDeadline =
        TimeSpan.FromSeconds(5);

    private async ValueTask<HostResult<AgentBrowserActionResult>>
        WaitForBrowserAsync(
            AgentBrowserDispatch dispatch,
            BrowserWaitRequest request,
            CancellationToken cancellationToken)
    {
        var completion = BrowserWaitCompletion.TimedOut;
        if (request.Condition is BrowserWaitCondition.Delay delay)
        {
            try
            {
                await Task.Delay(
                        delay.Value,
                        _timeProvider,
                        cancellationToken)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                completion = BrowserWaitCompletion.Matched;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                completion = BrowserWaitCompletion.Cancelled;
            }
        }
        else
        {
            completion = await WaitForBrowserConditionAsync(
                    dispatch.Browser,
                    request.Condition,
                    request.Timeout,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var final = await CaptureFinalBrowserWaitObservationAsync(
                dispatch,
                completion)
            .ConfigureAwait(false);
        return HostResult<AgentBrowserActionResult>.Succeed(
            new AgentBrowserActionResult.Wait(final),
            dispatch.Session.Snapshot().Descriptor.Revision);
    }

    private async ValueTask<BrowserWaitCompletion>
        WaitForBrowserConditionAsync(
            IBrowserPanelSession browser,
            BrowserWaitCondition condition,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        using var conditionDeadline = new CancellationTokenSource(
            timeout,
            _timeProvider);
        using var conditionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                conditionDeadline.Token);
        var pollInterval = BrowserWaitPollInterval;
        int? lastChangeToken = null;
        var observesNetworkActivity =
            condition is BrowserWaitCondition.NetworkIdle;
        var networkObservationStarted = false;
        try
        {
            if (observesNetworkActivity)
            {
                await browser.BeginNetworkActivityObservationAsync(
                        conditionCancellation.Token)
                    .AsTask()
                    .WaitAsync(conditionCancellation.Token)
                    .ConfigureAwait(false);
                networkObservationStarted = true;
            }

            while (true)
            {
                var observation = await ObserveBrowserWaitConditionAsync(
                        browser,
                        condition,
                        conditionCancellation.Token)
                    .AsTask()
                    // Session providers are required to observe
                    // cancellation, but the host deadline remains
                    // authoritative when a faulty provider does not.
                    .WaitAsync(conditionCancellation.Token)
                    .ConfigureAwait(false);
                if (observation.Status == BrowserWaitConditionStatus.Matched)
                {
                    return BrowserWaitCompletion.Matched;
                }

                if (observation.Status == BrowserWaitConditionStatus.SessionEnded)
                {
                    return BrowserWaitCompletion.SessionEnded;
                }

                pollInterval = lastChangeToken != observation.ChangeToken
                    ? BrowserWaitPollInterval
                    : TimeSpan.FromMilliseconds(Math.Min(
                        BrowserWaitMaximumPollInterval.TotalMilliseconds,
                        pollInterval.TotalMilliseconds * 2));
                lastChangeToken = observation.ChangeToken;
                await Task.Delay(
                        pollInterval,
                        _timeProvider,
                        conditionCancellation.Token)
                    .WaitAsync(conditionCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            return BrowserWaitCompletion.Cancelled;
        }
        catch (OperationCanceledException) when (
            conditionDeadline.IsCancellationRequested)
        {
            return BrowserWaitCompletion.TimedOut;
        }
        finally
        {
            if (networkObservationStarted)
            {
                using var cleanup = new CancellationTokenSource(
                    BrowserWaitFinalReadDeadline,
                    _timeProvider);
                try
                {
                    await browser.EndNetworkActivityObservationAsync(
                            cleanup.Token)
                        .AsTask()
                        .WaitAsync(cleanup.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Cleanup is best-effort after the wait's outcome is
                    // already known. Production renderers enqueue Network.disable
                    // synchronously before completing this operation.
                }
            }
        }
    }

    private async ValueTask<BrowserWaitConditionObservation>
        ObserveBrowserWaitConditionAsync(
            IBrowserPanelSession browser,
            BrowserWaitCondition condition,
            CancellationToken cancellationToken)
    {
        var state = browser.State;
        switch (condition)
        {
            case BrowserWaitCondition.LoadState loadState:
                return Observation(
                    state.LoadState == loadState.Value,
                    state.GetHashCode());
            case BrowserWaitCondition.UrlPattern pattern:
                return Observation(
                    MatchesUrlPattern(
                        state.Address.Value.AbsoluteUri,
                        pattern.Value),
                    state.GetHashCode());
            case BrowserWaitCondition.DocumentRevision revision:
                return Observation(
                    state.DocumentRevision > revision.After,
                    state.GetHashCode());
            case BrowserWaitCondition.Text text:
                return await ObserveBrowserTextAsync(
                        browser,
                        state,
                        text.Value,
                        cancellationToken)
                    .ConfigureAwait(false);
            case BrowserWaitCondition.ElementState element:
                return await ObserveBrowserElementStateAsync(
                        browser,
                        state,
                        element,
                        cancellationToken)
                    .ConfigureAwait(false);
            case BrowserWaitCondition.NetworkIdle idle:
                return await ObserveBrowserNetworkIdleAsync(
                        browser,
                        idle.QuietFor,
                        cancellationToken)
                    .ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(condition),
                    condition.GetType(),
                    "The browser wait condition is unsupported.");
        }
    }

    private static async ValueTask<BrowserWaitConditionObservation>
        ObserveBrowserTextAsync(
            IBrowserPanelSession browser,
            BrowserSessionState state,
            string text,
            CancellationToken cancellationToken)
    {
        if (state.LoadState != BrowserLoadState.Ready)
        {
            return Observation(matched: false, state.GetHashCode());
        }

        var snapshot = await browser.CaptureSnapshotAsync(
                BrowserDocumentBinding.FromState(state),
                cancellationToken)
            .ConfigureAwait(false);
        if (!snapshot.IsSuccess)
        {
            return snapshot.Error!.Code == BrowserErrorCode.SessionClosed
                ? SessionEndedObservation(state.GetHashCode())
                : Observation(matched: false, state.GetHashCode());
        }

        var changeToken = new HashCode();
        changeToken.Add(state);
        foreach (var node in snapshot.Value!.Nodes)
        {
            changeToken.Add(node.Role, StringComparer.Ordinal);
            changeToken.Add(node.Name, StringComparer.Ordinal);
            changeToken.Add(node.States);
        }

        return Observation(
            snapshot.Value.Nodes.Any(node =>
                node.Name.Contains(text, StringComparison.Ordinal)),
            changeToken.ToHashCode());
    }

    private static async ValueTask<BrowserWaitConditionObservation>
        ObserveBrowserElementStateAsync(
            IBrowserPanelSession browser,
            BrowserSessionState state,
            BrowserWaitCondition.ElementState condition,
            CancellationToken cancellationToken)
    {
        if (state.DocumentRevision != condition.SourceDocumentRevision
            || state.LoadState != BrowserLoadState.Ready)
        {
            return SessionEndedObservation(state.GetHashCode());
        }

        var reference = new BrowserElementReference(
            condition.Reference,
            BrowserDocumentBinding.FromState(state));
        var observation = await browser.ReadElementStateAsync(
                reference,
                cancellationToken)
            .ConfigureAwait(false);
        if (!observation.IsSuccess)
        {
            return observation.Error!.Code is
                BrowserErrorCode.ElementReferenceStale
                or BrowserErrorCode.SessionClosed
                or BrowserErrorCode.NavigationStateChanged
                    ? SessionEndedObservation(state.GetHashCode())
                    : Observation(matched: false, state.GetHashCode());
        }

        return Observation(
            observation.Value!.Read(condition.State) == condition.Expected,
            observation.Value.GetHashCode());
    }

    private static async ValueTask<BrowserWaitConditionObservation>
        ObserveBrowserNetworkIdleAsync(
            IBrowserPanelSession browser,
            TimeSpan quietFor,
            CancellationToken cancellationToken)
    {
        var observation = await browser.ReadNetworkActivityAsync(
                cancellationToken)
            .ConfigureAwait(false);
        if (!observation.IsSuccess)
        {
            return observation.Error!.Code == BrowserErrorCode.SessionClosed
                ? SessionEndedObservation(0)
                : Observation(matched: false, 0);
        }

        var value = observation.Value!;
        return Observation(
            value.IsObservable
                && value.ActiveRequestCount == 0
                && value.QuietFor >= quietFor,
            HashCode.Combine(value.IsObservable, value.ActiveRequestCount));
    }

    private async ValueTask<BrowserWaitOutcome>
        CaptureFinalBrowserWaitObservationAsync(
            AgentBrowserDispatch dispatch,
            BrowserWaitCompletion completion)
    {
        using var cleanup = new CancellationTokenSource(
            BrowserWaitFinalReadDeadline,
            _timeProvider);
        var state = dispatch.Browser.State;
        BrowserDocumentSnapshot? snapshot = null;
        BrowserError? snapshotError = null;
        try
        {
            if (state.LoadState == BrowserLoadState.Ready)
            {
                var result = await dispatch.Browser.CaptureSnapshotAsync(
                        BrowserDocumentBinding.FromState(state),
                        cleanup.Token)
                    .AsTask()
                    // The final observation has its own cleanup budget and
                    // must complete even when the condition phase was
                    // cancelled. Never trust a provider to honor its token.
                    .WaitAsync(cleanup.Token)
                    .ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    snapshot = result.Value;
                    state = dispatch.Browser.State;
                    if (!snapshot!.Document.Matches(state))
                    {
                        snapshot = null;
                        snapshotError = BrowserError.Create(
                            BrowserErrorCode.NavigationStateChanged,
                            "The browser changed during the final wait snapshot.",
                            retryable: true);
                    }
                }
                else
                {
                    snapshotError = result.Error;
                    state = dispatch.Browser.State;
                }
            }
            else
            {
                snapshotError = BrowserError.Create(
                    state.LoadState == BrowserLoadState.Failed
                        ? BrowserErrorCode.NavigationFailed
                        : BrowserErrorCode.NavigationInProgress,
                    "The browser document was not ready for the final wait snapshot.",
                    retryable: state.LoadState == BrowserLoadState.Loading);
            }
        }
        catch (OperationCanceledException)
        {
            state = dispatch.Browser.State;
            snapshotError = BrowserError.Create(
                BrowserErrorCode.Cancelled,
                "The final browser wait snapshot exceeded its bounded cleanup deadline.",
                retryable: true);
        }
        catch (Exception)
        {
            state = dispatch.Browser.State;
            snapshotError = BrowserError.Create(
                BrowserErrorCode.RendererUnavailable,
                "The final browser wait snapshot was unavailable.",
                retryable: true);
        }

        if (dispatch.Session.Snapshot().Descriptor.Lifecycle
            is SessionLifecycle.Closed or SessionLifecycle.Failed)
        {
            completion = BrowserWaitCompletion.SessionEnded;
        }

        return new BrowserWaitOutcome(
            completion,
            state,
            snapshot,
            snapshotError,
            _timeProvider.GetUtcNow());
    }

    internal static bool MatchesUrlPattern(string value, string pattern)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(pattern);
        var valueIndex = 0;
        var patternIndex = 0;
        var starIndex = -1;
        var retryValueIndex = -1;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?'
                    || pattern[patternIndex] == value[valueIndex]))
            {
                valueIndex++;
                patternIndex++;
                continue;
            }

            if (patternIndex < pattern.Length
                && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryValueIndex = valueIndex;
                continue;
            }

            if (starIndex < 0)
            {
                return false;
            }

            patternIndex = starIndex + 1;
            valueIndex = ++retryValueIndex;
        }

        while (patternIndex < pattern.Length
            && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    private static BrowserWaitConditionObservation Observation(
        bool matched,
        int changeToken) =>
        new(
            matched
                ? BrowserWaitConditionStatus.Matched
                : BrowserWaitConditionStatus.Pending,
            changeToken);

    private static BrowserWaitConditionObservation SessionEndedObservation(
        int changeToken) =>
        new(BrowserWaitConditionStatus.SessionEnded, changeToken);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct BrowserWaitConditionObservation(
        BrowserWaitConditionStatus Status,
        int ChangeToken);

    private enum BrowserWaitConditionStatus
    {
        Pending,
        Matched,
        SessionEnded,
    }
}
