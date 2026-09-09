using System.Diagnostics;
using Exclr8Cef;
using Exclr8Cef.Cdp;

namespace Asura.Browser;

/// <summary>
/// Tracks acknowledged CDP network lifecycle events for one CEF browser. It
/// remains unobservable until Network.enable succeeds, preventing a missing
/// subscription from being misreported as an idle page.
/// </summary>
internal sealed class CefBrowserNetworkActivityTracker : IDisposable
{
    private readonly object _gate = new();
    private readonly NetworkClient _network;
    private readonly HashSet<string> _activeRequests =
        new(StringComparer.Ordinal);
    private long _lastActivityTimestamp = Stopwatch.GetTimestamp();
    private long _observationGeneration;
    private int _observerCount;
    private bool _isObservable;
    private bool _disposed;

    public CefBrowserNetworkActivityTracker(CefBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);
        _network = browser.Network;
        _network.RequestWillBeSent += OnRequestWillBeSent;
        _network.LoadingFinished += OnLoadingFinished;
        _network.LoadingFailed += OnLoadingFailed;
    }

    public void BeginObservation()
    {
        long generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _observerCount++;
            if (_observerCount > 1)
            {
                return;
            }

            generation = ++_observationGeneration;
            _isObservable = false;
            _activeRequests.Clear();
        }

        // Network.enable sends every request lifecycle event through CDP and
        // parses it on the managed callback path. Only an active agent wait
        // owns that cost.
        _ = EnableAsync(generation);
    }

    public void EndObservation()
    {
        lock (_gate)
        {
            if (_disposed || _observerCount == 0)
            {
                return;
            }

            _observerCount--;
            if (_observerCount > 0)
            {
                return;
            }

            _observationGeneration++;
            _isObservable = false;
            _activeRequests.Clear();
        }

        _ = DisableAsync();
    }

    public NativeBrowserNetworkActivity Snapshot()
    {
        lock (_gate)
        {
            var quietFor = Stopwatch.GetElapsedTime(
                _lastActivityTimestamp,
                Stopwatch.GetTimestamp());
            return new NativeBrowserNetworkActivity(
                _isObservable && !_disposed,
                _activeRequests.Count,
                quietFor < TimeSpan.Zero ? TimeSpan.Zero : quietFor);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _observerCount = 0;
            _observationGeneration++;
            _isObservable = false;
            _activeRequests.Clear();
        }

        _network.RequestWillBeSent -= OnRequestWillBeSent;
        _network.LoadingFinished -= OnLoadingFinished;
        _network.LoadingFailed -= OnLoadingFailed;
    }

    private async Task EnableAsync(long generation)
    {
        try
        {
            await _network.EnableAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (!_disposed
                    && _observerCount > 0
                    && _observationGeneration == generation)
                {
                    _isObservable = true;
                    _lastActivityTimestamp = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (Exception)
        {
            // Snapshot remains explicitly unobservable. A wait therefore
            // continues or times out instead of claiming a false idle state.
        }
    }

    private async Task DisableAsync()
    {
        try
        {
            await _network.DisableAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Browser shutdown can win the race with cleanup. The renderer is
            // already closing in that case, so no live workload remains.
        }
    }

    private void OnRequestWillBeSent(
        object? sender,
        NetworkRequestEventArgs args)
    {
        if (string.IsNullOrEmpty(args.RequestId))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _observerCount == 0)
            {
                return;
            }

            _activeRequests.Add(args.RequestId);
            _lastActivityTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private void OnLoadingFinished(
        object? sender,
        NetworkLoadingFinishedEventArgs args) =>
        Finish(args.RequestId);

    private void OnLoadingFailed(
        object? sender,
        NetworkLoadingFailedEventArgs args) =>
        Finish(args.RequestId);

    private void Finish(string requestId)
    {
        lock (_gate)
        {
            if (_disposed || _observerCount == 0)
            {
                return;
            }

            _activeRequests.Remove(requestId);
            _lastActivityTimestamp = Stopwatch.GetTimestamp();
        }
    }
}
