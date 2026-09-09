using Asura.Application;

namespace Asura.Browser;

/// <summary>
/// Keeps one pending UI drain for observational browser chrome. Repeated
/// notices and progress carry their latest state; download start/terminal and
/// distinct security/lifecycle event kinds are retained independently.
/// </summary>
internal sealed class BrowserProductEventDispatch(
    Action<Action> post,
    Action<BrowserProductEvent> deliver) : IDisposable
{
    private const int MaximumEventsPerDrain = 64;
    private readonly object _gate = new();
    private readonly Dictionary<EventKey, PendingEvent> _pending = [];
    private readonly Action<Action> _post = post ?? throw new ArgumentNullException(nameof(post));
    private readonly Action<BrowserProductEvent> _deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
    private long _sequence;
    private bool _scheduled;
    private bool _disposed;

    public void Publish(BrowserProductEvent productEvent)
    {
        ArgumentNullException.ThrowIfNull(productEvent);
        var schedule = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var key = KeyFor(productEvent);
            // CEF normally orders these callbacks, but a late progress update
            // must never replace a pending completed/cancelled state.
            if (productEvent is BrowserProductEvent.DownloadProgressed
                && _pending.TryGetValue(key, out var existing)
                && existing.Event is BrowserProductEvent.DownloadCompleted or BrowserProductEvent.DownloadCancelled)
            {
                return;
            }

            _pending[key] = new(++_sequence, productEvent);
            if (!_scheduled)
            {
                _scheduled = true;
                schedule = true;
            }
        }

        if (schedule)
        {
            _post(Drain);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending.Clear();
        }
    }

    private void Drain()
    {
        PendingEvent[] batch;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            var selected = _pending.OrderBy(pair => pair.Value.Sequence)
                .Take(MaximumEventsPerDrain).ToArray();
            batch = [.. selected.Select(pair => pair.Value)];
            foreach (var pair in selected)
            {
                _pending.Remove(pair.Key);
            }
        }

        foreach (var item in batch)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }
            try
            {
                _deliver(item.Event);
            }
            catch
            {
                // Browser chrome is observational. A subscriber failure must
                // neither reach CEF nor strand the remaining notifications.
            }
        }

        lock (_gate)
        {
            if (_disposed || _pending.Count == 0)
            {
                _scheduled = false;
                return;
            }
        }
        _post(Drain);
    }

    private static EventKey KeyFor(BrowserProductEvent productEvent) => productEvent switch
    {
        BrowserProductEvent.DownloadRequested item => new(typeof(BrowserProductEvent.DownloadRequested), item.DownloadId),
        BrowserProductEvent.DownloadProgressed item => new(typeof(BrowserProductEvent.DownloadProgressed), item.DownloadId),
        BrowserProductEvent.DownloadCompleted item => new(typeof(BrowserProductEvent.DownloadProgressed), item.DownloadId),
        BrowserProductEvent.DownloadCancelled item => new(typeof(BrowserProductEvent.DownloadProgressed), item.DownloadId),
        _ => new(productEvent.GetType(), 0),
    };

    private readonly record struct EventKey(Type Kind, int DownloadId);
    private sealed record PendingEvent(long Sequence, BrowserProductEvent Event);
}
