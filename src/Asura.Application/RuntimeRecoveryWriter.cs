using System.Threading.Channels;

namespace Asura.Application;

/// <summary>
/// Serializes recovery writes produced by synchronous presentation mutations. Callers enqueue
/// immutable, versioned payloads and flush the queue before completing the application run marker.
/// </summary>
public sealed class RuntimeRecoveryWriter
{
    private const int MaximumScheduledKeys = RuntimeRecoveryInventory.MaximumSnapshotsPerRun;

    private readonly Lock _gate = new();
    private readonly IRuntimeRecoveryStore _store;
    private readonly ApplicationStartupState _startupState;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<string> _keyQueue;
    private readonly Dictionary<string, RuntimeRecoverySnapshot> _pendingByKey =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _scheduledKeys = new(StringComparer.Ordinal);
    private readonly Task _worker;
    private TaskCompletionSource _idle = CompletedSignal();
    private ApplicationRunError? _firstFailure;
    private bool _sealed;

    public RuntimeRecoveryWriter(
        IRuntimeRecoveryStore store,
        ApplicationStartupState startupState,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _startupState = startupState ?? throw new ArgumentNullException(nameof(startupState));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _keyQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(MaximumScheduledKeys)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = RunWorkerAsync();
    }

    public event EventHandler<RuntimeRecoveryWriteFailedEventArgs>? WriteFailed;

    public ApplicationRunResult<Unit> Enqueue(
        string key,
        int schemaVersion,
        string payloadJson)
    {
        if (_startupState.Run is not { RunId: { Length: > 0 } runId })
        {
            return Failure(
                ApplicationRunErrorCode.RunMismatch,
                "Runtime recovery cannot be written before the application run starts.");
        }

        if (string.IsNullOrWhiteSpace(key)
            || key.Length > 256
            || key.Any(char.IsControl)
            || schemaVersion <= 0
            || string.IsNullOrWhiteSpace(payloadJson))
        {
            return Failure(
                ApplicationRunErrorCode.StorageFailure,
                "Runtime recovery metadata is invalid.");
        }

        var snapshot = new RuntimeRecoverySnapshot(
            runId,
            key,
            schemaVersion,
            payloadJson,
            _timeProvider.GetUtcNow());
        lock (_gate)
        {
            if (_sealed)
            {
                return Failure(
                    ApplicationRunErrorCode.StorageFailure,
                    "Runtime recovery is sealed for application shutdown.");
            }

            _pendingByKey[key] = snapshot;
            if (_scheduledKeys.Contains(key))
            {
                return ApplicationRunResult<Unit>.Success(Unit.Value);
            }

            if (_scheduledKeys.Count == MaximumScheduledKeys)
            {
                _pendingByKey.Remove(key);
                var error = new ApplicationRunError(
                    ApplicationRunErrorCode.StorageFailure,
                    "Runtime recovery has reached its bounded pending-key limit.");
                _firstFailure ??= error;
                return ApplicationRunResult<Unit>.Failure(error);
            }

            if (_scheduledKeys.Count == 0)
            {
                _idle = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _scheduledKeys.Add(key);
            if (!_keyQueue.Writer.TryWrite(key))
            {
                _scheduledKeys.Remove(key);
                _pendingByKey.Remove(key);
                if (_scheduledKeys.Count == 0)
                {
                    _idle.TrySetResult();
                }

                var error = new ApplicationRunError(
                    ApplicationRunErrorCode.StorageFailure,
                    "Runtime recovery could not schedule an accepted snapshot.");
                _firstFailure ??= error;
                return ApplicationRunResult<Unit>.Failure(error);
            }
        }

        return ApplicationRunResult<Unit>.Success(Unit.Value);
    }

    public async ValueTask<ApplicationRunResult<Unit>> FlushAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task observed;
            lock (_gate)
            {
                if (_scheduledKeys.Count == 0)
                {
                    return CurrentResultLocked();
                }

                observed = _idle.Task;
            }

            try
            {
                await observed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failure(
                    ApplicationRunErrorCode.Cancelled,
                    "Waiting for runtime recovery persistence was cancelled.");
            }

            lock (_gate)
            {
                if (_scheduledKeys.Count == 0)
                {
                    return CurrentResultLocked();
                }
            }
        }
    }

    public async ValueTask<ApplicationRunResult<Unit>> SealAndFlushAsync(
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_sealed)
            {
                _sealed = true;
                _keyQueue.Writer.TryComplete();
            }
        }

        try
        {
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                return CurrentResultLocked();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                ApplicationRunErrorCode.Cancelled,
                "Waiting for runtime recovery persistence was cancelled.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(
                ApplicationRunErrorCode.StorageFailure,
                "Runtime recovery persistence could not be drained.");
        }
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            await foreach (var key in _keyQueue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await PersistKeyAsync(key).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var error = new ApplicationRunError(
                ApplicationRunErrorCode.StorageFailure,
                "Runtime recovery persistence could not be drained.");
            lock (_gate)
            {
                _firstFailure ??= error;
                _sealed = true;
                _keyQueue.Writer.TryComplete();
                _pendingByKey.Clear();
                _scheduledKeys.Clear();
                _idle.TrySetResult();
            }

            NotifyWriteFailed(error);
        }
    }

    private async Task PersistKeyAsync(string key)
    {
        while (true)
        {
            RuntimeRecoverySnapshot snapshot;
            lock (_gate)
            {
                if (!_pendingByKey.Remove(key, out snapshot!))
                {
                    CompleteKeyLocked(key);
                    return;
                }
            }

            ApplicationRunResult<Unit> result;
            try
            {
                result = await _store.SaveAsync(snapshot, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                result = Failure(
                    ApplicationRunErrorCode.StorageFailure,
                    "Runtime recovery state could not be saved.");
            }

            if (!result.IsSuccess)
            {
                lock (_gate)
                {
                    _firstFailure ??= result.Error!;
                }

                NotifyWriteFailed(result.Error!);
            }

            lock (_gate)
            {
                if (_pendingByKey.ContainsKey(key))
                {
                    continue;
                }

                CompleteKeyLocked(key);
                return;
            }
        }
    }

    private void CompleteKeyLocked(string key)
    {
        _scheduledKeys.Remove(key);
        if (_scheduledKeys.Count == 0)
        {
            _idle.TrySetResult();
        }
    }

    private ApplicationRunResult<Unit> CurrentResultLocked() =>
        _firstFailure is null
            ? ApplicationRunResult<Unit>.Success(Unit.Value)
            : ApplicationRunResult<Unit>.Failure(_firstFailure);

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private void NotifyWriteFailed(ApplicationRunError error)
    {
        var handlers = WriteFailed;
        if (handlers is null)
        {
            return;
        }

        var eventArgs = new RuntimeRecoveryWriteFailedEventArgs(error);
        foreach (EventHandler<RuntimeRecoveryWriteFailedEventArgs> handler
                 in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Recovery persistence and the clean-run marker must not depend on UI observers.
            }
        }
    }

    private static ApplicationRunResult<Unit> Failure(
        ApplicationRunErrorCode code,
        string message) =>
        ApplicationRunResult<Unit>.Failure(new ApplicationRunError(code, message));
}

public sealed class RuntimeRecoveryWriteFailedEventArgs : EventArgs
{
    public RuntimeRecoveryWriteFailedEventArgs(ApplicationRunError error)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public ApplicationRunError Error { get; }
}
