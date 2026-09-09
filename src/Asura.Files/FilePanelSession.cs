using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Asura.Application;
using Asura.Core;

namespace Asura.Files;

public sealed class FilePanelSession : IFilePanelSession
{
    private readonly object _gate = new();
    private readonly IFilePanelClient _filePanel;
    private readonly FilePanelLocation _initialLocation;
    private readonly IDisposable? _ownedPanelClient;
    private readonly HashSet<FilePanelTransferId> _ownedTransferIds = [];
    private readonly SemaphoreSlim _transferOperationGate = new(1, 1);
    private readonly IFileTransferQueueClient _transferQueue;
    private readonly Channel<PanelSessionEvent> _events = Channel.CreateUnbounded<PanelSessionEvent>(
        new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false,
        });
    private bool _closed;
    private bool _disposed;
    private long _sequence;

    public FilePanelSession(
        SessionId id,
        FilePanelLocation initialLocation,
        IFilePanelClient filePanel,
        IFileTransferQueueClient transferQueue,
        CapabilitySet capabilities,
        FileSessionMetadata metadata,
        IDisposable? ownedPanelClient = null)
    {
        Id = id;
        _initialLocation = initialLocation ?? throw new ArgumentNullException(nameof(initialLocation));
        _filePanel = filePanel ?? throw new ArgumentNullException(nameof(filePanel));
        _transferQueue = transferQueue ?? throw new ArgumentNullException(nameof(transferQueue));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        if (metadata.TrustedRoot != initialLocation)
        {
            throw new ArgumentException(
                "File-session metadata must bind the session's exact initial location.",
                nameof(metadata));
        }

        _ownedPanelClient = ownedPanelClient;
        _transferQueue.TransfersChanged += OnTransfersChanged;
        Publish(SessionLifecycle.Active, SessionHealth.Healthy, ReadyDetail());
    }

    public SessionId Id { get; }

    public PanelKind Kind => PanelKind.FileViewer;

    public CapabilitySet Capabilities { get; }

    public FileSessionMetadata Metadata { get; }

    public IReadOnlyList<FilePanelTransferSnapshot> Transfers
    {
        get
        {
            HashSet<FilePanelTransferId> owned;
            lock (_gate)
            {
                owned = [.. _ownedTransferIds];
            }

            return Array.AsReadOnly(_transferQueue.Transfers
                .Where(transfer => owned.Contains(transfer.Id))
                .OrderByDescending(transfer => transfer.QueuedAt)
                .ToArray());
        }
    }

    public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken) =>
        IsOpen()
            ? _filePanel.ListAsync(request, cancellationToken)
            : ValueTask.FromResult(Closed<FilePanelPage>());

    public IAsyncEnumerable<FilePanelResult<FilePanelEntry>> SearchAsync(
        FilePanelSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsOpen()
            ? _filePanel.SearchAsync(request, cancellationToken)
            : ClosedSearchAsync(cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken) =>
        IsOpen()
            ? _filePanel.StatAsync(location, cancellationToken)
            : ValueTask.FromResult(Closed<FilePanelEntry>());

    public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken) =>
        IsOpen()
            ? _filePanel.PreviewAsync(request, cancellationToken)
            : ValueTask.FromResult(Closed<FilePanelPreview>());

    public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken) =>
        IsOpen()
            ? _filePanel.CreateDirectoryAsync(request, cancellationToken)
            : ValueTask.FromResult(Closed<FilePanelEntry>());

    public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken) =>
        IsOpen()
            ? _filePanel.RenameAsync(request, cancellationToken)
            : ValueTask.FromResult(Closed<FilePanelEntry>());

    public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken) =>
        IsOpen()
            ? _filePanel.DeleteAsync(request, cancellationToken)
            : ValueTask.FromResult(Closed<FilePanelDeleteReceipt>());

    public ValueTask<FilePanelResult<FilePanelTextWriteReceipt>> WriteTextAsync(
        FilePanelTextWriteRequest request,
        CancellationToken cancellationToken) =>
        IsOpen()
            ? _filePanel.WriteTextAsync(request, cancellationToken)
            : ValueTask.FromResult(Closed<FilePanelTextWriteReceipt>());

    public ValueTask<FilePanelResult<FilePanelCopyReceipt>> CopyAsync(
        FilePanelCopyRequest request,
        CancellationToken cancellationToken) =>
        IsOpen()
            ? _filePanel.CopyAsync(request, cancellationToken)
            : ValueTask.FromResult(Closed<FilePanelCopyReceipt>());

    public ValueTask<FilePanelResult<FilePanelAccessControl>> GetAccessControlAsync(
        FilePanelAccessControlRequest request,
        CancellationToken cancellationToken) =>
        IsOpen()
            ? _filePanel.GetAccessControlAsync(request, cancellationToken)
            : ValueTask.FromResult(Closed<FilePanelAccessControl>());

    public ValueTask<FilePanelResult<FilePanelAccessControl>> SetAccessControlAsync(
        FilePanelSetAccessControlRequest request,
        CancellationToken cancellationToken) =>
        IsOpen()
            ? _filePanel.SetAccessControlAsync(request, cancellationToken)
            : ValueTask.FromResult(Closed<FilePanelAccessControl>());

    public async ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueTransferAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _transferOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen())
            {
                return Closed<FilePanelTransferSnapshot>();
            }

            var result = await _transferQueue.EnqueueAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (result.IsSuccess)
            {
                lock (_gate)
                {
                    _ownedTransferIds.Add(result.Value!.Id);
                }

                PublishTransferState();
            }

            return result;
        }
        finally
        {
            _transferOperationGate.Release();
        }
    }

    public async ValueTask<FilePanelResult<Unit>> CancelTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        await _transferOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen())
            {
                return Closed<Unit>();
            }

            if (!Owns(id))
            {
                return TransferNotOwned<Unit>();
            }

            return await _transferQueue.CancelAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transferOperationGate.Release();
        }
    }

    public async ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        await _transferOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen())
            {
                return Closed<FilePanelTransferSnapshot>();
            }

            if (!Owns(id))
            {
                return TransferNotOwned<FilePanelTransferSnapshot>();
            }

            var result = await _transferQueue.RetryAsync(id, cancellationToken).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                lock (_gate)
                {
                    _ownedTransferIds.Add(result.Value!.Id);
                }

                PublishTransferState();
            }

            return result;
        }
        finally
        {
            _transferOperationGate.Release();
        }
    }

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_closed)
            {
                return ValueTask.FromResult(new PanelSessionSnapshot(
                    SessionLifecycle.Closed,
                    SessionHealth.Ended,
                    false,
                    "The file panel session is closed."));
            }
        }

        var activeCount = Transfers.Count(transfer => transfer.CanCancel);
        return ValueTask.FromResult(new PanelSessionSnapshot(
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            activeCount > 0,
            activeCount > 0
                ? $"{activeCount} file transfer(s) are active."
                : ReadyDetail()));
    }

    public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var sessionEvent in _events.Reader
            .ReadAllAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (sessionEvent.Sequence > afterSequence)
            {
                yield return sessionEvent;
            }
        }
    }

    public async ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        await _transferOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_closed)
                {
                    return PanelCloseOutcome.AlreadyClosed;
                }
            }

            var activeTransfers = Transfers.Where(transfer => transfer.CanCancel).ToArray();
            if (mode == PanelCloseMode.Graceful && activeTransfers.Length > 0)
            {
                return PanelCloseOutcome.ConfirmationRequired;
            }

            if (mode == PanelCloseMode.Force)
            {
                foreach (var transfer in activeTransfers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var cancelled = await _transferQueue
                        .CancelAsync(transfer.Id, cancellationToken)
                        .ConfigureAwait(false);
                    if (!cancelled.IsSuccess
                        && _transferQueue.Transfers.Any(item =>
                            item.Id == transfer.Id && item.CanCancel))
                    {
                        return PanelCloseOutcome.EngineFailed;
                    }
                }
            }

            MarkClosed();
            return mode == PanelCloseMode.Force
                ? PanelCloseOutcome.ForceTerminated
                : PanelCloseOutcome.GracefullyClosed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PanelCloseOutcome.Cancelled;
        }
        finally
        {
            _transferOperationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _ = await CloseAsync(PanelCloseMode.Force, CancellationToken.None).ConfigureAwait(false);
        _transferQueue.TransfersChanged -= OnTransfersChanged;
        _events.Writer.TryComplete();
        _ownedPanelClient?.Dispose();
        _transferOperationGate.Dispose();
    }

    private bool IsOpen()
    {
        lock (_gate)
        {
            return !_closed && !_disposed;
        }
    }

    private bool Owns(FilePanelTransferId id)
    {
        lock (_gate)
        {
            return _ownedTransferIds.Contains(id);
        }
    }

    private void MarkClosed()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
        }

        _transferQueue.TransfersChanged -= OnTransfersChanged;
        Publish(SessionLifecycle.Closed, SessionHealth.Ended, "File panel session closed.");
        _events.Writer.TryComplete();
    }

    private void OnTransfersChanged(object? sender, EventArgs args)
    {
        _ = sender;
        _ = args;
        if (IsOpen())
        {
            PublishTransferState();
        }
    }

    private void PublishTransferState()
    {
        var activeCount = Transfers.Count(transfer => transfer.CanCancel);
        Publish(
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            activeCount > 0
                ? $"{activeCount} file transfer(s) are active."
                : ReadyDetail());
    }

    private void Publish(
        SessionLifecycle lifecycle,
        SessionHealth health,
        string detail)
    {
        PanelSessionEvent sessionEvent;
        lock (_gate)
        {
            _sequence++;
            sessionEvent = new PanelSessionEvent(
                _sequence,
                lifecycle,
                health,
                DateTimeOffset.UtcNow,
                detail);
        }

        _events.Writer.TryWrite(sessionEvent);
    }

    private string ReadyDetail() =>
        $"File panel ready for profile '{_initialLocation.ProviderProfileId}'.";

    private static FilePanelResult<T> Closed<T>() => FilePanelResult<T>.Failure(
        new FilePanelError(
            FilePanelErrorCode.Cancelled,
            "file_panel_session_closed",
            "The file panel session is closed.",
            false));

    private static async IAsyncEnumerable<FilePanelResult<FilePanelEntry>>
        ClosedSearchAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return Closed<FilePanelEntry>();
        await Task.CompletedTask;
    }

    private static FilePanelResult<T> TransferNotOwned<T>() => FilePanelResult<T>.Failure(
        new FilePanelError(
            FilePanelErrorCode.NotFound,
            "file_transfer_not_owned_by_panel",
            "The transfer does not belong to this file panel session.",
            false));
}
