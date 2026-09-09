using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Asura.Application;

namespace Asura.Infrastructure;

internal sealed class CalciteSqlLanguageSession : ISqlLanguageSession
{
    private static readonly TimeSpan CatalogRequestTimeout = TimeSpan.FromSeconds(30);
    private readonly SqlLanguageWorkerLaunch _launch;
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly object _stderrLock = new();
    private readonly StringBuilder _stderrTail = new();
    private SqlCatalogSnapshot _catalog;
    private Process? _process;
    private Task? _stderrDrain;
    private CancellationTokenSource? _stderrCancellation;
    private long _nextRequestId;
    private bool _initialized;
    private bool _available;
    private bool _disposed;
    private bool _permanentFailure;
    private int _consecutiveFailures;
    private long _retryAfterUtcTicks;
    private string? _unavailableReason = null;

    public CalciteSqlLanguageSession(
        SqlLanguageWorkerLaunch launch,
        SqlCatalogSnapshot catalog,
        TimeSpan requestTimeout)
    {
        _launch = launch;
        _catalog = catalog;
        _requestTimeout = requestTimeout;
    }

    public bool IsAvailable => Volatile.Read(ref _available) && !_disposed;

    public bool CanRetry => !_disposed
        && !Volatile.Read(ref _permanentFailure)
        && DateTime.UtcNow.Ticks >= Volatile.Read(ref _retryAfterUtcTicks);

    public string? UnavailableReason => Volatile.Read(ref _unavailableReason);

    internal async Task TryInitializeAsync(CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requests.Release();
        }
    }

    public Task<SqlCompletionResult> CompleteAsync(
        string sql,
        int cursorOffset,
        CancellationToken cancellationToken) =>
        CompleteAsync(
            sql,
            cursorOffset,
            SqlCompletionContext.Empty,
            cancellationToken);

    public async Task<SqlCompletionResult> CompleteAsync(
        string sql,
        int cursorOffset,
        SqlCompletionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(cursorOffset);
        if (cursorOffset > sql.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(cursorOffset));
        }

        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var response = await RequestWithRestartAsync(
                    "complete",
                    new WorkerRequestParameters(
                        Sql: sql,
                        CursorOffset: cursorOffset,
                        PreferredObject: context.PreferredObject is { } preferred
                            ? SqlLanguageWorkerProtocol.ObjectId(preferred)
                            : null),
                    cancellationToken)
                .ConfigureAwait(false);
            if (response is null)
            {
                return EmptyCompletion(cursorOffset);
            }

            if (response?.Error is not null)
            {
                await HandleOperationErrorAsync("completion", response.Error)
                    .ConfigureAwait(false);
                return EmptyCompletion(cursorOffset);
            }

            try
            {
                var completion = MapCompletion(
                    sql,
                    cursorOffset,
                    CompletionResult(response));
                ResetFailureState();
                return completion;
            }
            catch (SqlLanguageProtocolException)
            {
                SetRecoverableFailure(
                    "The SQL intelligence worker returned an invalid completion result.");
                await StopProcessAsync().ConfigureAwait(false);
                return EmptyCompletion(cursorOffset);
            }
        }
        finally
        {
            _requests.Release();
        }
    }

    public async Task<IReadOnlyList<SqlDiagnostic>> DiagnoseAsync(
        string sql,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sql);
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var response = await RequestWithRestartAsync(
                    "diagnose",
                    new WorkerRequestParameters(Sql: sql),
                    cancellationToken)
                .ConfigureAwait(false);
            if (response is null)
            {
                return [];
            }

            if (response?.Error is not null)
            {
                await HandleOperationErrorAsync("diagnostics", response.Error)
                    .ConfigureAwait(false);
                return [];
            }

            try
            {
                var diagnostics = MapDiagnostics(sql, DiagnosticResult(response));
                ResetFailureState();
                return diagnostics;
            }
            catch (SqlLanguageProtocolException)
            {
                SetRecoverableFailure(
                    "The SQL intelligence worker returned an invalid diagnostic result.");
                await StopProcessAsync().ConfigureAwait(false);
                return [];
            }
        }
        finally
        {
            _requests.Release();
        }
    }

    public async Task UpdateCatalogAsync(
        SqlCatalogSnapshot catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var response = await RequestWithRestartAsync(
                    "updateCatalog",
                    new WorkerRequestParameters(
                        Catalog: SqlLanguageWorkerProtocol.Catalog(catalog)),
                    cancellationToken)
                .ConfigureAwait(false);
            if (response is { Error: null })
            {
                // Commit only after the worker accepted its own atomic catalog
                // replacement. If it rejects the snapshot, a later restart
                // must still restore the previous known-good catalog.
                _catalog = catalog;
                ResetFailureState();
            }
            else if (response?.Error is not null)
            {
                await HandleOperationErrorAsync("catalog update", response.Error)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _requests.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _requests.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (IsProcessRunning())
            {
                await ExchangeAsync("shutdown", null, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await StopProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _requests.Release();
        }
    }

    private async Task<WorkerResponseEnvelope?> RequestWithRestartAsync(
        string method,
        WorkerRequestParameters? parameters,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable && !CanRetry)
        {
            return null;
        }

        if (!await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var response = await ExchangeAsync(method, parameters, cancellationToken)
            .ConfigureAwait(false);
        if (response is not null || cancellationToken.IsCancellationRequested)
        {
            return response;
        }

        // A worker crash cannot corrupt application state: restart it, restore
        // the detached catalog, and retry the interrupted operation once.
        if (!await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await ExchangeAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized && IsProcessRunning())
        {
            return true;
        }

        await StopProcessAsync().ConfigureAwait(false);
        if (!StartProcess())
        {
            SetRecoverableFailure("The SQL intelligence worker could not be started.");
            return false;
        }

        var response = await ExchangeAsync(
                "initialize",
                new WorkerRequestParameters(
                    Catalog: SqlLanguageWorkerProtocol.Catalog(_catalog)),
                cancellationToken)
            .ConfigureAwait(false);
        if (response is null || response.Error is not null)
        {
            if (response?.Error is { } error)
            {
                SetPermanentFailure(WorkerError("initialize", error));
            }
            else if (UnavailableReason is null)
            {
                SetRecoverableFailure(
                    "The SQL intelligence worker stopped during initialization.");
            }
            await StopProcessAsync().ConfigureAwait(false);
            return false;
        }

        _initialized = true;
        ClearInitializationFailure();
        Volatile.Write(ref _unavailableReason, null);
        Volatile.Write(ref _available, true);
        return true;
    }

    private bool StartProcess()
    {
        lock (_stderrLock)
        {
            _stderrTail.Clear();
        }

        var process = _launch.CreateProcess();
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return false;
            }
        }
        catch (Exception exception) when (exception is
            Win32Exception or FileNotFoundException or UnauthorizedAccessException)
        {
            process.Dispose();
            return false;
        }

        _process = process;
        _stderrCancellation = new CancellationTokenSource();
        _stderrDrain = DrainStderrAsync(
            process.StandardError.BaseStream,
            _stderrCancellation.Token);
        return true;
    }

    private async Task<WorkerResponseEnvelope?> ExchangeAsync(
        string method,
        WorkerRequestParameters? parameters,
        CancellationToken cancellationToken)
    {
        if (_process is not { } process || !IsProcessRunning())
        {
            SetRecoverableFailure(
                $"The SQL intelligence worker stopped before {method}.");
            return null;
        }

        var id = checked(++_nextRequestId);
        var request = new WorkerRequestEnvelope(
            SqlLanguageWorkerProtocol.Version,
            id,
            method,
            parameters);
        var timeoutDuration = method is "initialize" or "updateCatalog"
            ? CatalogRequestTimeout
            : _requestTimeout;
        using var timeout = new CancellationTokenSource(timeoutDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            var payload = SqlLanguageWorkerProtocol.Serialize(request);
            await SqlLanguageWorkerProtocol.WriteFrameAsync(
                    process.StandardInput.BaseStream,
                    payload,
                    linked.Token)
                .ConfigureAwait(false);
            var responsePayload = await SqlLanguageWorkerProtocol.ReadFrameAsync(
                    process.StandardOutput.BaseStream,
                    linked.Token)
                .ConfigureAwait(false);
            var response = SqlLanguageWorkerProtocol.Deserialize(responsePayload);
            if (response.Version != SqlLanguageWorkerProtocol.Version || response.Id != id)
            {
                throw new SqlLanguageProtocolException(
                    "SQL language response did not match its request.");
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or IOException or EndOfStreamException
            or ObjectDisposedException or InvalidOperationException
            or SqlLanguageProtocolException)
        {
            SetRecoverableFailure(exception is OperationCanceledException
                ? $"The SQL intelligence worker timed out during {method}."
                : $"The SQL intelligence worker failed during {method}: {exception.Message}");
            await StopProcessAsync().ConfigureAwait(false);
            return null;
        }
    }

    private async Task StopProcessAsync()
    {
        Volatile.Write(ref _available, false);
        _initialized = false;
        var process = _process;
        var stderrDrain = _stderrDrain;
        var stderrCancellation = _stderrCancellation;
        _process = null;
        _stderrDrain = null;
        _stderrCancellation = null;
        if (process is null)
        {
            return;
        }

        stderrCancellation?.Cancel();
        TryKill(process);
        process.StandardInput.Dispose();
        if (stderrDrain is not null)
        {
            try
            {
                await stderrDrain.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        stderrCancellation?.Dispose();
        process.Dispose();
    }

    private bool IsProcessRunning()
    {
        try
        {
            return _process is { HasExited: false };
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task DrainStderrAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken)
                   .ConfigureAwait(false)) > 0)
        {
            AppendStderr(Encoding.UTF8.GetString(buffer, 0, bytesRead));
        }
    }

    private void AppendStderr(string value)
    {
        const int maximumCharacters = 4096;
        lock (_stderrLock)
        {
            _stderrTail.Append(value);
            if (_stderrTail.Length > maximumCharacters)
            {
                _stderrTail.Remove(0, _stderrTail.Length - maximumCharacters);
            }
        }
    }

    private void SetUnavailableReason(string reason)
    {
        string stderr;
        lock (_stderrLock)
        {
            stderr = _stderrTail.ToString();
        }

        var combined = string.IsNullOrWhiteSpace(stderr)
            ? reason
            : $"{reason} Worker detail: {stderr}";
        var sanitized = new string([.. combined.Select(character => char.IsControl(character) ? ' ' : character)]);
        const int maximumCharacters = 320;
        if (sanitized.Length > maximumCharacters)
        {
            sanitized = sanitized[..(maximumCharacters - 1)] + "…";
        }

        Volatile.Write(ref _unavailableReason, sanitized);
    }

    private void SetPermanentFailure(string reason)
    {
        Volatile.Write(ref _permanentFailure, true);
        Volatile.Write(ref _retryAfterUtcTicks, DateTime.MaxValue.Ticks);
        SetUnavailableReason(reason);
    }

    private void SetRecoverableFailure(string reason)
    {
        Volatile.Write(ref _permanentFailure, false);
        var failures = Math.Min(6, checked(++_consecutiveFailures));
        var delayMilliseconds = Math.Min(5000, 250 * (1 << (failures - 1)));
        Volatile.Write(
            ref _retryAfterUtcTicks,
            DateTime.UtcNow.AddMilliseconds(delayMilliseconds).Ticks);
        SetUnavailableReason(reason);
    }

    private void ResetFailureState()
    {
        Volatile.Write(ref _permanentFailure, false);
        _consecutiveFailures = 0;
        Volatile.Write(ref _retryAfterUtcTicks, 0);
    }

    private void ClearInitializationFailure()
    {
        Volatile.Write(ref _permanentFailure, false);
        Volatile.Write(ref _retryAfterUtcTicks, 0);
    }

    private async Task HandleOperationErrorAsync(
        string operation,
        WorkerResponseError error)
    {
        switch (error.Code)
        {
            case "invalidParams":
            case "invalidCatalog":
                // The worker remained healthy and rejected only this request.
                ResetFailureState();
                return;
            case "unsupportedVersion":
            case "methodNotFound":
            case "invalidEnvelope":
            case "invalidRequest":
                SetPermanentFailure(WorkerError(operation, error));
                break;
            default:
                SetRecoverableFailure(WorkerError(operation, error));
                break;
        }

        await StopProcessAsync().ConfigureAwait(false);
    }

    private static string WorkerError(string method, WorkerResponseError error)
    {
        var code = string.IsNullOrWhiteSpace(error.Code) ? "workerError" : error.Code;
        var message = string.IsNullOrWhiteSpace(error.Message)
            ? "No detail was returned."
            : error.Message;
        return $"The SQL intelligence worker rejected {method} ({code}): {message}";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception)
        {
        }
    }

    private static SqlCompletionResult MapCompletion(
        string sql,
        int cursorOffset,
        WorkerCompletionResult? result)
    {
        if (result?.ReplacementStart is not { } start
            || result.ReplacementLength is not { } length
            || result.Items is null)
        {
            throw new SqlLanguageProtocolException(
                "SQL language completion result is incomplete.");
        }

        if (start < 0 || length < 0 || start > sql.Length || length > sql.Length - start)
        {
            throw new SqlLanguageProtocolException(
                "SQL language completion range is outside the document.");
        }

        var items = new SqlCompletionItem[result.Items.Count];
        for (var index = 0; index < result.Items.Count; index++)
        {
            var item = result.Items[index];
            if (item.Label is null || item.InsertText is null)
            {
                throw new SqlLanguageProtocolException(
                    "SQL language completion item is incomplete.");
            }

            items[index] = new SqlCompletionItem(
                item.Label,
                CompletionKind(item.Kind),
                item.Detail,
                item.InsertText);
        }

        return new SqlCompletionResult(start, length, items);
    }

    private static IReadOnlyList<SqlDiagnostic> MapDiagnostics(
        string sql,
        WorkerDiagnosticResult? result)
    {
        if (result?.Items is null)
        {
            throw new SqlLanguageProtocolException(
                "SQL language diagnostic result is incomplete.");
        }

        var diagnostics = new SqlDiagnostic[result.Items.Count];
        for (var index = 0; index < result.Items.Count; index++)
        {
            var diagnostic = result.Items[index];
            if (diagnostic.Message is null
                || diagnostic.Start < 0
                || diagnostic.Length < 0
                || diagnostic.Start > sql.Length
                || diagnostic.Length > sql.Length - diagnostic.Start)
            {
                throw new SqlLanguageProtocolException(
                    "SQL language diagnostic is outside the document.");
            }

            diagnostics[index] = new SqlDiagnostic(
                diagnostic.Message,
                DiagnosticSeverity(diagnostic.Severity),
                diagnostic.Start,
                diagnostic.Length,
                diagnostic.Code);
        }

        return diagnostics;
    }

    private static WorkerCompletionResult? CompletionResult(
        WorkerResponseEnvelope? response)
    {
        try
        {
            return response?.Result is { } result
                ? System.Text.Json.JsonSerializer.Deserialize(
                    result,
                    SqlLanguageWorkerJsonContext.Default.WorkerCompletionResult)
                : null;
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new SqlLanguageProtocolException(
                "SQL language completion result is malformed.",
                exception);
        }
    }

    private static WorkerDiagnosticResult? DiagnosticResult(
        WorkerResponseEnvelope? response)
    {
        try
        {
            return response?.Result is { } result
                ? System.Text.Json.JsonSerializer.Deserialize(
                    result,
                    SqlLanguageWorkerJsonContext.Default.WorkerDiagnosticResult)
                : null;
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new SqlLanguageProtocolException(
                "SQL language diagnostic result is malformed.",
                exception);
        }
    }

    private static SqlCompletionItemKind CompletionKind(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "keyword" => SqlCompletionItemKind.Keyword,
            "catalog" => SqlCompletionItemKind.Catalog,
            "schema" => SqlCompletionItemKind.Schema,
            "table" => SqlCompletionItemKind.Table,
            "view" => SqlCompletionItemKind.View,
            "column" => SqlCompletionItemKind.Column,
            "function" => SqlCompletionItemKind.Function,
            "datatype" or "data_type" => SqlCompletionItemKind.DataType,
            _ => SqlCompletionItemKind.Other,
        };

    private static SqlDiagnosticSeverity DiagnosticSeverity(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "error" => SqlDiagnosticSeverity.Error,
            "warning" => SqlDiagnosticSeverity.Warning,
            _ => SqlDiagnosticSeverity.Information,
        };

    private static SqlCompletionResult EmptyCompletion(int cursorOffset) =>
        new(cursorOffset, 0, []);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
