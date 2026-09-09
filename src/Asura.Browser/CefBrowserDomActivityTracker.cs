using System.Diagnostics;
using System.Text.Json;
using Asura.Application;
using Exclr8Cef;

namespace Asura.Browser;

/// <summary>
/// Observes Chromium's DOM domain so detached browser work can wait for a
/// rendered document to become quiet without injecting timers or observers
/// into the page's JavaScript world.
/// </summary>
internal sealed class CefBrowserDomActivityTracker : IDisposable
{
    private readonly object _gate = new();
    private readonly CefBrowser _browser;
    private TaskCompletionSource<long> _activityChanged = NewActivitySignal();
    private long _lastActivityTimestamp = Stopwatch.GetTimestamp();
    private long _activityGeneration;
    private long _documentGeneration;
    private bool _isActive;
    private bool _isObservable;
    private bool _disposed;

    public CefBrowserDomActivityTracker(CefBrowser browser)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _browser.DevToolsMessage += OnDevToolsMessage;
    }

    public async Task<bool> BeginObservationAsync()
    {
        long documentGeneration;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _isActive = true;
            _isObservable = false;
            documentGeneration = ++_documentGeneration;
            RecordActivityLocked();
        }

        try
        {
            await _browser.ExecuteDevToolsMethodAsync("DOM.enable", null)
                .ConfigureAwait(false);
            return await RefreshDocumentAsync(documentGeneration)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnosticProjection.WriteTrace(
                "browser.dom-observation.start-failed",
                exception);
            return false;
        }
    }

    public long MarkActivity()
    {
        lock (_gate)
        {
            return RecordActivityLocked();
        }
    }

    public async Task<long> WaitForQuietAsync(
        TimeSpan quietWindow,
        CancellationToken cancellationToken)
    {
        if (quietWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietWindow));
        }

        while (true)
        {
            var snapshot = Snapshot();
            if (snapshot.IsObservable && snapshot.QuietFor >= quietWindow)
            {
                return snapshot.Generation;
            }

            var activity = WaitForActivityAfterAsync(
                snapshot.Generation,
                cancellationToken);
            if (!snapshot.IsObservable)
            {
                await activity.ConfigureAwait(false);
                continue;
            }

            var quietBoundary = Task.Delay(
                quietWindow - snapshot.QuietFor,
                cancellationToken);
            var completed = await Task.WhenAny(activity, quietBoundary)
                .ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
    }

    public Task<long> WaitForActivityAfterAsync(
        long generation,
        CancellationToken cancellationToken)
    {
        Task<long> activity;
        lock (_gate)
        {
            if (_activityGeneration > generation)
            {
                return Task.FromResult(_activityGeneration);
            }

            activity = _activityChanged.Task;
        }

        return activity.WaitAsync(cancellationToken);
    }

    public void EndObservation()
    {
        lock (_gate)
        {
            if (_disposed || !_isActive)
            {
                return;
            }

            _isActive = false;
            _isObservable = false;
            _documentGeneration++;
            RecordActivityLocked();
        }

        _ = DisableAsync();
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
            _isActive = false;
            _isObservable = false;
            _documentGeneration++;
            RecordActivityLocked();
        }

        _browser.DevToolsMessage -= OnDevToolsMessage;
    }

    internal static bool IsDomActivityMessage(string json) =>
        ReadDomActivityMethod(json) is not null;

    private static string? ReadDomActivityMethod(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("method", out var methodValue)
                || methodValue.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var method = methodValue.GetString();
            return method is
                "DOM.attributeModified"
                or "DOM.attributeRemoved"
                or "DOM.characterDataModified"
                or "DOM.childNodeCountUpdated"
                or "DOM.childNodeInserted"
                or "DOM.childNodeRemoved"
                or "DOM.documentUpdated"
                or "DOM.pseudoElementAdded"
                or "DOM.pseudoElementRemoved"
                or "DOM.setChildNodes"
                or "DOM.shadowRootPopped"
                or "DOM.shadowRootPushed"
                    ? method
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void OnDevToolsMessage(
        object? sender,
        DevToolsMessageEventArgs eventArgs)
    {
        _ = sender;
        if (!eventArgs.IsEvent
            || ReadDomActivityMethod(eventArgs.Json) is not { } method)
        {
            return;
        }

        var documentUpdated = string.Equals(
            method,
            "DOM.documentUpdated",
            StringComparison.Ordinal);
        long documentGeneration = 0;
        lock (_gate)
        {
            if (_disposed || !_isActive)
            {
                return;
            }

            if (documentUpdated)
            {
                _isObservable = false;
                documentGeneration = ++_documentGeneration;
            }

            RecordActivityLocked();
        }

        if (documentGeneration > 0)
        {
            _ = RefreshDocumentAfterUpdateAsync(documentGeneration);
        }
    }

    private async Task RefreshDocumentAfterUpdateAsync(long documentGeneration)
    {
        try
        {
            await RefreshDocumentAsync(documentGeneration).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnosticProjection.WriteTrace(
                "browser.dom-observation.refresh-failed",
                exception);
        }
    }

    private async Task<bool> RefreshDocumentAsync(long documentGeneration)
    {
        await _browser.ExecuteDevToolsMethodAsync(
                "DOM.getDocument",
                "{\"depth\":-1,\"pierce\":true}")
            .ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed
                || !_isActive
                || documentGeneration != _documentGeneration)
            {
                return false;
            }

            _isObservable = true;
            RecordActivityLocked();
            return true;
        }
    }

    private DomActivitySnapshot Snapshot()
    {
        lock (_gate)
        {
            var quietFor = Stopwatch.GetElapsedTime(
                _lastActivityTimestamp,
                Stopwatch.GetTimestamp());
            return new DomActivitySnapshot(
                _isObservable && !_disposed,
                _activityGeneration,
                quietFor < TimeSpan.Zero ? TimeSpan.Zero : quietFor);
        }
    }

    private long RecordActivityLocked()
    {
        _lastActivityTimestamp = Stopwatch.GetTimestamp();
        var generation = ++_activityGeneration;
        var previous = _activityChanged;
        _activityChanged = NewActivitySignal();
        previous.TrySetResult(generation);
        return generation;
    }

    private async Task DisableAsync()
    {
        try
        {
            await _browser.ExecuteDevToolsMethodAsync("DOM.disable", null)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            SecretSafeDiagnosticProjection.WriteTrace(
                "browser.dom-observation.stop-failed",
                exception);
        }
    }

    private static TaskCompletionSource<long> NewActivitySignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record DomActivitySnapshot(
        bool IsObservable,
        long Generation,
        TimeSpan QuietFor);
}
