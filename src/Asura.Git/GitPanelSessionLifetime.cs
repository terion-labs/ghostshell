using System.Runtime.CompilerServices;
using Asura.Application;
using Asura.Core;

namespace Asura.Git;

internal sealed class GitPanelSessionLifetime : IAsyncDisposable
{
    private const int MaximumRetainedEvents = 64;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _closeCancellation = new();
    private readonly List<PanelSessionEvent> _events = [];
    private readonly TimeProvider _timeProvider;
    private TaskCompletionSource _eventsChanged = NewSignal();
    private bool _closed;
    private bool _disposed;
    private long _sequence;

    public GitPanelSessionLifetime(
        SessionId id,
        CapabilitySet capabilities,
        TimeProvider timeProvider)
    {
        Id = id;
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Publish(SessionLifecycle.Active, SessionHealth.Healthy, "Git repository is ready.");
    }

    public SessionId Id { get; }

    public CapabilitySet Capabilities { get; }

    public CancellationTokenSource CreateOperationCancellation(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _closed || _disposed
                    ? new CancellationToken(canceled: true)
                    : _closeCancellation.Token);
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
                    "The Git session is closed.")
                : new PanelSessionSnapshot(
                    SessionLifecycle.Active,
                    SessionHealth.Healthy,
                    false,
                    "Git repository is ready."));
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
                pending = [.. _events.Where(item => item.Sequence > afterSequence)];
                completed = _closed;
                waitForChange = _eventsChanged.Task;
            }

            foreach (var item in pending)
            {
                afterSequence = item.Sequence;
                yield return item;
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
        Task cancellation;
        lock (_gate)
        {
            if (_closed)
            {
                return PanelCloseOutcome.AlreadyClosed;
            }

            _closed = true;
            PublishUnsafe(SessionLifecycle.Closed, SessionHealth.Ended, "Git session closed.");
            cancellation = ObserveCancellationAsync(_closeCancellation.CancelAsync());
        }

        await cancellation.ConfigureAwait(false);
        return mode == PanelCloseMode.Force
            ? PanelCloseOutcome.ForceTerminated
            : PanelCloseOutcome.GracefullyClosed;
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

        _ = await CloseAsync(PanelCloseMode.Force, CancellationToken.None)
            .ConfigureAwait(false);
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

    private void Publish(SessionLifecycle lifecycle, SessionHealth health, string detail)
    {
        lock (_gate)
        {
            PublishUnsafe(lifecycle, health, detail);
        }
    }

    private void PublishUnsafe(SessionLifecycle lifecycle, SessionHealth health, string detail)
    {
        _sequence++;
        _events.Add(new PanelSessionEvent(
            _sequence,
            lifecycle,
            health,
            _timeProvider.GetUtcNow(),
            detail));
        if (_events.Count > MaximumRetainedEvents)
        {
            _events.RemoveRange(0, _events.Count - MaximumRetainedEvents);
        }

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
            // A provider callback cannot keep a hosted panel alive after close.
        }
    }
}
