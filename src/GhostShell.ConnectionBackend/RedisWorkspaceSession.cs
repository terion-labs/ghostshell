using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.ConnectionBackend;

/// <summary>Starts a live Redis session in an owned workspace process, including explicit SSH service workspaces.</summary>
internal sealed class RedisWorkspaceSessionFactory(
    Func<ConnectionProfile?, CancellationToken, Task<DatabaseWorkspaceOperationLaunch>> launch) : IRedisPanelSessionFactory
{
    public async Task<IRedisPanelSession> OpenAsync(string connectionString, ConnectionProfile? tunnel, CancellationToken cancellationToken)
    {
        var owned = await launch(tunnel, cancellationToken).ConfigureAwait(false);
        RedisWorkspaceSession? session = null;
        try
        {
            session = new RedisWorkspaceSession(owned);
            await session.OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await RedisWorkspaceSession.CleanupAfterFailureAsync(session is null
                ? owned.CleanupAsync : () => session.DisposeAsync().AsTask()).ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// The desktop facade owns one child and one serialized command stream. Cancellation
/// after dispatch closes that stream: a Redis mutation must never be replayed to recover it.
/// </summary>
internal sealed partial class RedisWorkspaceSession : IRedisPanelSession
{
    private readonly DatabaseWorkspaceOperationLaunch _owned;
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _termination;
    private readonly SemaphoreSlim _command = new(1, 1);
    private readonly Channel<RedisPubSubMessage> _messages = Channel.CreateBounded<RedisPubSubMessage>(
        new BoundedChannelOptions(RedisWorkspaceProtocol.MaximumQueuedEvents) { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource _readerFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _errors;
    private readonly object _closeLock = new();
    private Task? _close;
    private PendingResponse? _pending;
    private long _nextId;
    private RedisServerFacts _facts = new(null, null, RedisTopologyKind.Unknown, RedisLogicalDatabaseMode.Unknown,
        0, null, false, false, false, false);

    internal RedisWorkspaceSession(DatabaseWorkspaceOperationLaunch owned)
    {
        _owned = owned;
        owned.Lifetime.ThrowIfCancellationRequested();
        var start = owned.StartInfo;
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.CreateNoWindow = true;
        _process = Process.Start(start) ?? throw new IOException("The Redis backend could not start.");
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(owned.Lifetime);
        _termination = _lifetime.Token.Register(() => DatabaseOperationWorker.StopOwnedProcess(_process));
        _errors = DrainErrorsAsync();
        _ = DeliverMessagesAsync();
        _ = ReadResponsesAsync();
    }

    public RedisServerFacts Facts => Volatile.Read(ref _facts);

    public event EventHandler<RedisPubSubMessage>? MessageReceived;

    internal async Task OpenAsync(string connectionString, CancellationToken token) =>
        _ = await InvokeAsync(new(0, RedisWorkspaceOperation.Open, Text: connectionString), token).ConfigureAwait(false);

    private async Task<RedisWorkspaceResponse> InvokeAsync(RedisWorkspaceRequest request, CancellationToken token)
    {
        await _command.WaitAsync(token).ConfigureAwait(false);
        var dispatched = false;
        var confirmed = false;
        byte[]? serialized = null;
        try
        {
            ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            var id = checked(++_nextId);
            serialized = await RedisWorkspaceProtocol.SerializeAsync(request with { Id = id },
                RedisWorkspaceJsonContext.Default.RedisWorkspaceRequest, cancellation.Token).ConfigureAwait(false);
            var pending = new PendingResponse(id, new(TaskCreationOptions.RunContinuationsAsynchronously));
            Volatile.Write(ref _pending, pending);
            cancellation.Token.ThrowIfCancellationRequested();
            // Set before writing because a failed pipe write may still have reached Redis.
            dispatched = true;
            await RedisWorkspaceProtocol.WriteAsync(_process.StandardInput.BaseStream, serialized, cancellation.Token).ConfigureAwait(false);
            var response = await pending.Completion.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            if (response.Failure != RedisWorkspaceFailure.None)
            {
                confirmed = response.Failure is not (RedisWorkspaceFailure.Unavailable or RedisWorkspaceFailure.OutcomeUnknown);
                throw FailureException(response.Failure);
            }
            var facts = response.Facts ?? throw new InvalidDataException("The Redis backend omitted its session facts.");
            ValidateResult(request.Operation, response);
            Volatile.Write(ref _facts, facts);
            confirmed = true;
            return response;
        }
        catch (Exception exception)
        {
            // Validation/rejection responses describe a completed request. Transport
            // failures close the session before returning, including its guest lease.
            if ((dispatched && !confirmed) || _lifetime.IsCancellationRequested)
            {
                await CleanupAfterFailureAsync(() => DisposeAsync().AsTask()).ConfigureAwait(false);
            }
            if (dispatched && !confirmed && RedisWorkspaceProtocol.Mutates(request.Operation))
            {
                throw new DatabaseMutationOutcomeUnknownException(
                    "The Redis backend stopped before confirming the mutation. It may have completed. Reload and verify before retrying.", exception);
            }
            throw;
        }
        finally
        {
            if (serialized is not null) { CryptographicOperations.ZeroMemory(serialized); }
            Volatile.Write(ref _pending, null);
            _command.Release();
        }
    }

    private async Task ReadResponsesAsync()
    {
        try
        {
            while (true)
            {
                var response = await RedisWorkspaceProtocol.ReadAsync(_process.StandardOutput.BaseStream,
                    RedisWorkspaceJsonContext.Default.RedisWorkspaceResponse, _lifetime.Token).ConfigureAwait(false);
                if (!Enum.IsDefined(response.Failure)) { throw new InvalidDataException("The Redis backend response is invalid."); }
                if (response.Message is { } message)
                {
                    if (response.Id != 0 || response.Failure != RedisWorkspaceFailure.None || response.Facts is not null
                        || response.Scan is not null || response.Key is not null || response.Search is not null || response.Indexes is not null
                        || Encoding.UTF8.GetByteCount(message.Payload) > RedisWorkspaceProtocol.MaximumEventBytes
                        || !_messages.Writer.TryWrite(message))
                    {
                        throw new InvalidDataException("The Redis subscription exceeded its delivery budget.");
                    }
                    continue;
                }
                var pending = Volatile.Read(ref _pending);
                if (pending is null || pending.Id != response.Id || !pending.Completion.TrySetResult(response))
                {
                    throw new InvalidDataException("The Redis backend response does not match its request.");
                }
            }
        }
        catch (Exception exception)
        {
            Volatile.Read(ref _pending)?.Completion.TrySetException(exception);
            await _lifetime.CancelAsync().ConfigureAwait(false);
            DatabaseOperationWorker.StopOwnedProcess(_process);
            _messages.Writer.TryComplete();
        }
        finally
        {
            _readerFinished.TrySetResult();
            // An idle connection can fail without a caller waiting for a response.
            // Release its guest lease as well, rather than delaying workspace teardown.
            _ = CleanupAfterFailureAsync(() => DisposeAsync().AsTask());
        }
    }

    private async Task DeliverMessagesAsync()
    {
        try
        {
            await foreach (var message in _messages.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try { MessageReceived?.Invoke(this, message); }
                catch (Exception)
                {
                    SecretSafeDiagnosticProjection.WriteStandardError("redis.workspace.subscriber.failed", SecretSafeDiagnosticKind.Unexpected);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task DrainErrorsAsync()
    {
        try { await _process.StandardError.BaseStream.CopyToAsync(Stream.Null, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) when (_lifetime.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_closeLock) { return new ValueTask(_close ??= CloseAsync()); }
    }

    private async Task CloseAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        DatabaseOperationWorker.StopOwnedProcess(_process);
        try
        {
            await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            await _readerFinished.Task.ConfigureAwait(false);
            await _errors.ConfigureAwait(false);
        }
        finally
        {
            _messages.Writer.TryComplete();
            await _termination.DisposeAsync().ConfigureAwait(false);
            _process.Dispose();
            // The independent cleanup call runs only after the owned process is reaped.
            try { await _owned.CleanupAsync().ConfigureAwait(false); }
            finally { _lifetime.Dispose(); }
        }
    }

    internal static async Task CleanupAfterFailureAsync(Func<Task> cleanup)
    {
        try { await cleanup().ConfigureAwait(false); }
        catch (Exception)
        {
            SecretSafeDiagnosticProjection.WriteStandardError("redis.workspace.cleanup.failed", SecretSafeDiagnosticKind.Unexpected);
        }
    }

    private static Exception FailureException(RedisWorkspaceFailure failure) => failure switch
    {
        RedisWorkspaceFailure.InvalidRequest => new ArgumentException("The Redis operation arguments are invalid."),
        RedisWorkspaceFailure.Unsupported => new NotSupportedException("The Redis server does not support this operation."),
        RedisWorkspaceFailure.Rejected => new InvalidOperationException("The Redis server rejected this operation. Check the key type, permissions, and supplied values."),
        _ => new IOException("The Redis backend connection is unavailable. Reconnect before running another operation."),
    };

    private static void ValidateResult(RedisWorkspaceOperation operation, RedisWorkspaceResponse response)
    {
        var valid = operation switch
        {
            RedisWorkspaceOperation.ScanKeys => response.Scan is { Keys: not null },
            RedisWorkspaceOperation.ReadKey => response.Key is { Summary: not null, Entries: not null },
            RedisWorkspaceOperation.ListSearchIndexes => response.Indexes is not null,
            RedisWorkspaceOperation.Search => response.Search is { Values: not null },
            RedisWorkspaceOperation.RemoveEntry => Enum.IsDefined(response.Removal),
            _ => true,
        };
        if (!valid) { throw new InvalidDataException("The Redis backend omitted its operation result."); }
    }

    private sealed record PendingResponse(long Id, TaskCompletionSource<RedisWorkspaceResponse> Completion);
}
