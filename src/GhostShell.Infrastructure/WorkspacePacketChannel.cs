using System.Security.Cryptography;
using GhostShell.Application;

namespace GhostShell.Infrastructure;

/// <summary>
/// An authenticated, ordered packet stream between a workspace guest TUN
/// device and its host-owned network route. The caller may run one read and one
/// write concurrently, but must not run two reads or two writes concurrently.
/// Frames authenticate their contents but do not encrypt them. The transport
/// must therefore remain on the workspace's host-only network. Disposing the
/// channel cancels active operations and erases its derived authentication
/// keys, but does not dispose the transport stream.
/// </summary>
public sealed partial class WorkspacePacketChannel : IAsyncDisposable
{
    public const int AuthenticationKeyLength = 32;

    private readonly Stream _transport;
    private readonly byte[] _readKey;
    private readonly byte[] _writeKey;
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ulong _nextReadSequence;
    private ulong _nextWriteSequence;
    private LifecycleState _state;
    private int _activeOperations;
    private TaskCompletionSource? _disposeCompletion;

    private WorkspacePacketChannel(
        Stream transport,
        byte[] readKey,
        byte[] writeKey,
        ulong nextReadSequence,
        ulong nextWriteSequence,
        WorkspaceGuestNetworkConfiguration configuration)
    {
        _transport = transport;
        _readKey = readKey;
        _writeKey = writeKey;
        _nextReadSequence = nextReadSequence;
        _nextWriteSequence = nextWriteSequence;
        Configuration = configuration;
    }

    public WorkspaceGuestNetworkConfiguration Configuration { get; }

    /// <summary>
    /// Creates caller-owned key material. The caller must erase the returned
    /// array after both peers have derived their directional session keys.
    /// </summary>
    public static byte[] CreateAuthenticationKey() =>
        RandomNumberGenerator.GetBytes(AuthenticationKeyLength);

    public async ValueTask SendPacketAsync(
        WorkspaceIpPacketFrame packet,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        cancellationToken.ThrowIfCancellationRequested();
        var operation = BeginOperation(cancellationToken);
        try
        {
            await WorkspacePacketFrameCodec.WriteAsync(
                    _transport,
                    _writeKey,
                    WorkspacePacketFrameCodec.MessageKind.IpPacket,
                    _nextWriteSequence,
                    packet.Packet,
                    operation.Token)
                .ConfigureAwait(false);
            _nextWriteSequence++;
        }
        catch
        {
            Fault();
            throw;
        }
        finally
        {
            operation.Dispose();
            EndOperation();
        }
    }

    public async ValueTask<WorkspaceIpPacketFrame> ReceivePacketAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operation = BeginOperation(cancellationToken);
        try
        {
            var frame = await WorkspacePacketFrameCodec.ReadAsync(
                    _transport,
                    _readKey,
                    _nextReadSequence,
                    operation.Token)
                .ConfigureAwait(false);
            RequireKind(frame, WorkspacePacketFrameCodec.MessageKind.IpPacket);
            _nextReadSequence++;
            try
            {
                return new WorkspaceIpPacketFrame(frame.Payload);
            }
            catch (ArgumentException exception)
            {
                throw new WorkspacePacketChannelProtocolException(
                    WorkspacePacketChannelFailure.MalformedPayload,
                    "The packet-channel payload is not a valid IP packet.",
                    exception);
            }
        }
        catch
        {
            Fault();
            throw;
        }
        finally
        {
            operation.Dispose();
            EndOperation();
        }
    }

    public ValueTask DisposeAsync()
    {
        bool eraseNow;
        Task completion;
        lock (_lifecycleGate)
        {
            if (_disposeCompletion is not null)
            {
                return new ValueTask(_disposeCompletion.Task);
            }

            _state = LifecycleState.Disposed;
            _disposeCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _disposeCompletion.Task;
            eraseNow = _activeOperations == 0;
        }

        _lifetime.Cancel();
        if (eraseNow)
        {
            CompleteDisposal();
        }

        return new ValueTask(completion);
    }

    private CancellationTokenSource BeginOperation(CancellationToken cancellationToken)
    {
        lock (_lifecycleGate)
        {
            if (_state == LifecycleState.Disposed)
            {
                throw new ObjectDisposedException(nameof(WorkspacePacketChannel));
            }

            if (_state == LifecycleState.Faulted)
            {
                throw new WorkspacePacketChannelProtocolException(
                    WorkspacePacketChannelFailure.ChannelFaulted,
                    "The packet channel cannot be reused after an incomplete or invalid frame.");
            }

            var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            _activeOperations++;
            return operation;
        }
    }

    private void EndOperation()
    {
        bool completeDisposal;
        lock (_lifecycleGate)
        {
            _activeOperations--;
            completeDisposal = _state == LifecycleState.Disposed
                && _activeOperations == 0;
        }

        if (completeDisposal)
        {
            CompleteDisposal();
        }
    }

    private void Fault()
    {
        bool cancelLifetime;
        lock (_lifecycleGate)
        {
            cancelLifetime = _state == LifecycleState.Active;
            if (cancelLifetime)
            {
                _state = LifecycleState.Faulted;
            }
        }

        if (cancelLifetime)
        {
            _lifetime.Cancel();
        }
    }

    private void CompleteDisposal()
    {
        CryptographicOperations.ZeroMemory(_readKey);
        CryptographicOperations.ZeroMemory(_writeKey);
        _lifetime.Dispose();
        _disposeCompletion!.SetResult();
    }

    private static void RequireKind(
        WorkspacePacketFrameCodec.Frame frame,
        WorkspacePacketFrameCodec.MessageKind expectedKind)
    {
        if (frame.Kind != expectedKind)
        {
            throw new WorkspacePacketChannelProtocolException(
                WorkspacePacketChannelFailure.UnexpectedMessage,
                $"Expected {expectedKind}, received {frame.Kind}.");
        }
    }

    private enum LifecycleState
    {
        Active,
        Faulted,
        Disposed,
    }
}
