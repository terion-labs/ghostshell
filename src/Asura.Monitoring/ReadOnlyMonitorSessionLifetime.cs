using System.Runtime.CompilerServices;
using Asura.Application;
using Asura.Core;

namespace Asura.Monitoring;

internal sealed class ReadOnlyMonitorSessionLifetime : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closeCancellation = new();
    private readonly List<PanelSessionEvent> _events = [];
    private readonly TimeProvider _timeProvider;
    private Task _closeCancellationTask = Task.CompletedTask;
    private TaskCompletionSource _eventsChanged = NewSignal();
    private bool _closed;
    private bool _disposed;
    private long _sequence;

    public ReadOnlyMonitorSessionLifetime(
        SessionId id,
        PanelKind kind,
        CapabilitySet capabilities,
        string readyDetail,
        TimeProvider timeProvider)
    {
        Id = id;
        Kind = kind;
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        ReadyDetail = string.IsNullOrWhiteSpace(readyDetail)
            ? throw new ArgumentException("A monitoring status is required.", nameof(readyDetail))
            : readyDetail;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Publish(SessionLifecycle.Active, SessionHealth.Healthy, ReadyDetail);
    }

    public SessionId Id { get; }

    public PanelKind Kind { get; }

    public CapabilitySet Capabilities { get; }

    public string ReadyDetail { get; }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return !_closed && !_disposed;
            }
        }
    }

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_closed
                ? new PanelSessionSnapshot(
                    SessionLifecycle.Closed,
                    SessionHealth.Ended,
                    false,
                    "The monitoring session is closed.")
                : new PanelSessionSnapshot(
                    SessionLifecycle.Active,
                    SessionHealth.Healthy,
                    false,
                    ReadyDetail));
        }
    }

    public CancellationTokenSource CreateOperationCancellation(
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closed || _disposed)
            {
                return CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    new CancellationToken(canceled: true));
            }

            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _closeCancellation.Token);
        }
    }

    public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            PanelSessionEvent[] pending;
            Task waitForChange;
            bool completed;
            lock (_gate)
            {
                pending = [.. _events.Where(sessionEvent => sessionEvent.Sequence > afterSequence)];
                completed = _closed;
                waitForChange = _eventsChanged.Task;
            }

            foreach (var sessionEvent in pending)
            {
                afterSequence = sessionEvent.Sequence;
                yield return sessionEvent;
            }

            if (completed)
            {
                yield break;
            }

            await waitForChange.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PanelCloseOutcome outcome;
        Task closeCancellationTask;
        lock (_gate)
        {
            if (_closed)
            {
                outcome = PanelCloseOutcome.AlreadyClosed;
                closeCancellationTask = _closeCancellationTask;
            }
            else
            {
                _closed = true;
                PublishUnsafe(
                    SessionLifecycle.Closed,
                    SessionHealth.Ended,
                    "Monitoring stopped.");
                _closeCancellationTask = ObserveCancellationAsync(
                    _closeCancellation.CancelAsync());
                closeCancellationTask = _closeCancellationTask;
                outcome = mode == PanelCloseMode.Force
                    ? PanelCloseOutcome.ForceTerminated
                    : PanelCloseOutcome.GracefullyClosed;
            }
        }

        await closeCancellationTask.ConfigureAwait(false);
        return outcome;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _ = await CloseAsync(PanelCloseMode.Force, CancellationToken.None).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _closeCancellation.Dispose();
    }

    private void Publish(
        SessionLifecycle lifecycle,
        SessionHealth health,
        string detail)
    {
        lock (_gate)
        {
            PublishUnsafe(lifecycle, health, detail);
        }
    }

    private void PublishUnsafe(
        SessionLifecycle lifecycle,
        SessionHealth health,
        string detail)
    {
        _sequence++;
        _events.Add(new PanelSessionEvent(
            _sequence,
            lifecycle,
            health,
            _timeProvider.GetUtcNow(),
            detail));
        var changed = _eventsChanged;
        _eventsChanged = NewSignal();
        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A snapshot source can register arbitrary callbacks. Closing the
            // monitor remains reliable even if one of those callbacks fails.
        }
    }
}
