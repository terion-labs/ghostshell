using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost;

public sealed partial class InMemorySessionHostClient
{
    public async ValueTask<HostResult<SessionSnapshot>> EnsureFilePanelSessionAsync(
        EnsureFilePanelSessionRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.InitialLocation);
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();

        var fingerprint = Fingerprint(
            ApplicationOperations.FilesOpen,
            request.SessionId.Value,
            request.Owner.PanelId.Value,
            LocationKey(request.InitialLocation));
        if (TryReplay(context, fingerprint, 0, out HostResult<SessionSnapshot>? replay))
        {
            return replay;
        }

        var invalid = ValidateContext<SessionSnapshot>(context, cancellationToken, 0);
        if (invalid is not null)
        {
            return invalid;
        }

        if (_filePanelFactory is null)
        {
            return Unsupported<SessionSnapshot>(
                "This session host has no file-panel session factory.",
                0);
        }

        try
        {
            await _sessionGraphGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<SessionSnapshot>(0);
        }

        try
        {
            ThrowIfDisposed();
            if (WorkspaceGraphFailure<SessionSnapshot>(
                    _workspaceGraphs.ValidateSessionOwner(
                        request.Owner,
                        PanelKind.FileViewer)) is { } ownerFailure)
            {
                return ownerFailure;
            }

            if (TryReplay(
                    context,
                    fingerprint,
                    0,
                    out HostResult<SessionSnapshot>? inGateReplay))
            {
                return inGateReplay;
            }

            using var operationCancellation = HostedOperationCancellation.Create(
                context,
                cancellationToken,
                _timeProvider);
            if (operationCancellation.Token.IsCancellationRequested)
            {
                return operationCancellation.DeadlineElapsed
                    ? DeadlineExceeded<SessionSnapshot>(0)
                    : Cancelled<SessionSnapshot>(0);
            }

            if (TryGetSession(request.SessionId, out var existing))
            {
                var existingSnapshot = existing.Snapshot();
                if (existingSnapshot.Descriptor.Owner != request.Owner
                    || existing.Engine.Kind != PanelKind.FileViewer)
                {
                    return HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.InvalidRequest,
                            "The requested session ID already belongs to another panel or session kind."),
                        existingSnapshot.Descriptor.Revision);
                }

                if (existingSnapshot.Descriptor.FileMetadata
                        is not { } existingFileMetadata
                    || existing.Engine is not IFilePanelSession existingFiles
                    || existingFiles.Metadata != existingFileMetadata
                    || existingFileMetadata.TrustedRoot
                        != request.InitialLocation)
                {
                    return HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.InvalidRequest,
                            "The requested session ID is already bound to a different trusted file root."),
                        existingSnapshot.Descriptor.Revision);
                }

                if (existingSnapshot.Descriptor.Lifecycle is
                    SessionLifecycle.Closed or SessionLifecycle.Failed)
                {
                    return ClosedSession<SessionSnapshot>(
                        existingSnapshot.Descriptor.Revision);
                }

                var existingReservation = ReserveReplay<SessionSnapshot>(
                    context,
                    fingerprint,
                    existingSnapshot.Descriptor.Revision,
                    out var existingOutcomeReserved);
                if (existingReservation is not null)
                {
                    return existingReservation;
                }

                if (WorkspaceGraphFailure<SessionSnapshot>(
                        _workspaceGraphs.LinkSession(
                            request.Owner,
                            PanelKind.FileViewer,
                            request.SessionId)) is { } existingLinkFailure)
                {
                    return existingOutcomeReserved
                        ? OutcomeUncertain<SessionSnapshot>(
                            existingSnapshot.Descriptor.Revision)
                        : existingLinkFailure;
                }

                var existingResult = HostResult<SessionSnapshot>.Succeed(
                    existingSnapshot,
                    existingSnapshot.Descriptor.Revision);
                CompleteReplay(context, fingerprint, existingResult);
                return existingResult;
            }

            var reservationReplay = ReserveReplay<SessionSnapshot>(
                context,
                fingerprint,
                currentRevision: 0,
                out var outcomeReserved);
            if (reservationReplay is not null)
            {
                return reservationReplay;
            }

            IFilePanelSession? createdEngine = null;
            FileSessionMetadata createdFileMetadata;
            HostedSession hosted;
            try
            {
                createdEngine = await _filePanelFactory
                    .CreateAsync(
                        request.Owner.WorkspaceId,
                        request.SessionId,
                        request.InitialLocation,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
                createdFileMetadata = createdEngine.Metadata;
                if (createdFileMetadata.TrustedRoot != request.InitialLocation)
                {
                    await DisposeUnownedSessionAsync(createdEngine).ConfigureAwait(false);
                    createdEngine = null;
                    var mismatch = HostResult<SessionSnapshot>.Fail(
                        HostError.Create(
                            HostErrorCode.EngineFailed,
                            "The File Viewer engine did not bind the requested trusted root."),
                        currentRevision: 0);
                    return outcomeReserved
                        ? OutcomeUncertain<SessionSnapshot>(0)
                        : mismatch;
                }

                var engineSnapshot = await createdEngine
                    .SnapshotAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                hosted = new HostedSession(
                    createdEngine,
                    request.Owner,
                    request.Title,
                    engineSnapshot,
                    _eventRetention,
                    _timeProvider,
                    fileMetadata: createdFileMetadata);
                lock (_gate)
                {
                    _sessions.Add(request.SessionId, hosted);
                }

                createdEngine = null;
            }
            catch (OperationCanceledException)
            {
                await DisposeUnownedSessionAsync(createdEngine).ConfigureAwait(false);
                return outcomeReserved
                    ? OutcomeUncertain<SessionSnapshot>(0)
                    : operationCancellation.DeadlineElapsed
                        ? DeadlineExceeded<SessionSnapshot>(0)
                        : Cancelled<SessionSnapshot>(0);
            }
            catch (Exception exception)
            {
                await DisposeUnownedSessionAsync(createdEngine).ConfigureAwait(false);
                return outcomeReserved
                    ? OutcomeUncertain<SessionSnapshot>(0)
                    : EngineFailure<SessionSnapshot>(exception, 0);
            }

            if (WorkspaceGraphFailure<SessionSnapshot>(
                    _workspaceGraphs.LinkSession(
                        request.Owner,
                        PanelKind.FileViewer,
                        request.SessionId)) is { } linkFailure)
            {
                var rejected = await RemoveRejectedSessionAsync(hosted, linkFailure)
                    .ConfigureAwait(false);
                return outcomeReserved
                    ? OutcomeUncertain<SessionSnapshot>(0)
                    : rejected;
            }

            var snapshot = hosted.Snapshot();
            var result = HostResult<SessionSnapshot>.Succeed(
                snapshot,
                snapshot.Descriptor.Revision);
            CompleteReplay(context, fingerprint, result);
            return result;
        }
        finally
        {
            _sessionGraphGate.Release();
        }
    }

    public ValueTask<HostResult<FilePanelResult<FilePanelPage>>> ListFilesAsync(
        FilePanelListHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Request);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteFileOperationAsync(
            request.SessionId,
            ApplicationOperations.FilesList,
            LocationKey(request.Request.Location),
            context,
            cancellationToken,
            changesState: false,
            (files, token) => files.ListAsync(request.Request, token));
    }

    public ValueTask<HostResult<FilePanelResult<FilePanelEntry>>> StatFileAsync(
        FilePanelStatHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Location);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteFileOperationAsync(
            request.SessionId,
            ApplicationOperations.FilesStat,
            LocationKey(request.Location),
            context,
            cancellationToken,
            changesState: false,
            (files, token) => files.StatAsync(request.Location, token));
    }

    public ValueTask<HostResult<FilePanelResult<FilePanelPreview>>> PreviewFileAsync(
        FilePanelPreviewHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Request);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteFileOperationAsync(
            request.SessionId,
            ApplicationOperations.FilesPreview,
            LocationKey(request.Request.Location),
            context,
            cancellationToken,
            changesState: false,
            (files, token) => files.PreviewAsync(request.Request, token));
    }

    public ValueTask<HostResult<FilePanelResult<FilePanelEntry>>> CreateFileDirectoryAsync(
        FilePanelCreateDirectoryHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Request);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteFileOperationAsync(
            request.SessionId,
            ApplicationOperations.FilesCreateDirectory,
            $"{LocationKey(request.Request.Location)}:{PreconditionKey(request.Request.Precondition)}",
            context,
            cancellationToken,
            changesState: true,
            (files, token) => files.CreateDirectoryAsync(request.Request, token));
    }

    public ValueTask<HostResult<FilePanelResult<FilePanelEntry>>> RenameFileAsync(
        FilePanelRenameHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Request);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteFileOperationAsync(
            request.SessionId,
            ApplicationOperations.FilesRename,
            $"{LocationKey(request.Request.Source)}:{LocationKey(request.Request.Destination)}:"
                + PreconditionKey(request.Request.DestinationPrecondition),
            context,
            cancellationToken,
            changesState: true,
            (files, token) => files.RenameAsync(request.Request, token));
    }

    /// <summary>
    /// Reading who can do what changes nothing, so it is governed as a read;
    /// changing it is a mutation like any other, and is audited as one.
    /// </summary>
    public ValueTask<HostResult<FilePanelResult<FilePanelAccessControl>>> GetFileAccessControlAsync(
        FilePanelAccessControlHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Request);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteFileOperationAsync(
            request.SessionId,
            ApplicationOperations.FilesReadAccessControl,
            LocationKey(request.Request.Location),
            context,
            cancellationToken,
            changesState: false,
            (files, token) => files.GetAccessControlAsync(request.Request, token));
    }

    public ValueTask<HostResult<FilePanelResult<FilePanelAccessControl>>> SetFileAccessControlAsync(
        FilePanelSetAccessControlHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Request);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteFileOperationAsync(
            request.SessionId,
            ApplicationOperations.FilesWriteAccessControl,
            $"{LocationKey(request.Request.Location)}:{request.Request.Mode?.Octal ?? "grants"}",
            context,
            cancellationToken,
            changesState: true,
            (files, token) => files.SetAccessControlAsync(request.Request, token));
    }

    public ValueTask<HostResult<FilePanelResult<FilePanelDeleteReceipt>>> DeleteFileAsync(
        FilePanelDeleteHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Request);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteFileOperationAsync(
            request.SessionId,
            ApplicationOperations.FilesDelete,
            $"{LocationKey(request.Request.Location)}:{request.Request.Recursive}:"
                + PreconditionKey(request.Request.Precondition),
            context,
            cancellationToken,
            changesState: true,
            (files, token) => files.DeleteAsync(request.Request, token));
    }

    public ValueTask<HostResult<FilePanelResult<FilePanelTransferSnapshot>>> EnqueueFileTransferAsync(
        FilePanelTransferEnqueueHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Request);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteFileOperationAsync(
            request.SessionId,
            ApplicationOperations.FilesTransferEnqueue,
            TransferKey(request.Request),
            context,
            cancellationToken,
            changesState: true,
            (files, token) => files.EnqueueTransferAsync(request.Request, token));
    }

    public ValueTask<HostResult<FilePanelResult<Unit>>> CancelFileTransferAsync(
        FilePanelTransferCancelHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteFileOperationAsync(
            request.SessionId,
            ApplicationOperations.FilesTransferCancel,
            request.TransferId.ToString(),
            context,
            cancellationToken,
            changesState: true,
            (files, token) => files.CancelTransferAsync(request.TransferId, token));
    }

    public ValueTask<HostResult<FilePanelResult<FilePanelTransferSnapshot>>> RetryFileTransferAsync(
        FilePanelTransferRetryHostRequest request,
        OperationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return ExecuteFileOperationAsync(
            request.SessionId,
            ApplicationOperations.FilesTransferRetry,
            request.TransferId.ToString(),
            context,
            cancellationToken,
            changesState: true,
            (files, token) => files.RetryTransferAsync(request.TransferId, token));
    }

    private async ValueTask<HostResult<FilePanelResult<T>>> ExecuteFileOperationAsync<T>(
        SessionId sessionId,
        string operationName,
        string operationKey,
        OperationContext context,
        CancellationToken cancellationToken,
        bool changesState,
        Func<IFilePanelSession, CancellationToken, ValueTask<FilePanelResult<T>>> operation)
    {
        var useIdempotencyGate = changesState && context.IdempotencyKey is not null;
        if (useIdempotencyGate)
        {
            try
            {
                await _idempotentFileOperationGate
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Cancelled<FilePanelResult<T>>(CurrentRevision(sessionId));
            }
        }

        try
        {
            var fingerprint = Fingerprint(operationName, sessionId.Value, operationKey);
            var initialRevision = CurrentRevision(sessionId);
            if (changesState
                && TryReplay(
                    context,
                    fingerprint,
                    initialRevision,
                    out HostResult<FilePanelResult<T>>? replay))
            {
                return replay;
            }

            if (!TryGetFilePanel(
                    sessionId,
                    out var session,
                    out var filePanel,
                    out HostResult<FilePanelResult<T>>? failure))
            {
                return failure;
            }

            var revision = session.Snapshot().Descriptor.Revision;
            var invalid = ValidateContext<FilePanelResult<T>>(
                context,
                cancellationToken,
                revision);
            if (invalid is not null)
            {
                return invalid;
            }

            if (RevisionConflict(
                    context,
                    session,
                    out HostResult<FilePanelResult<T>>? conflict))
            {
                return conflict;
            }

            using var operationCancellation = HostedOperationCancellation.Create(
                context,
                cancellationToken,
                _timeProvider);
            if (operationCancellation.Token.IsCancellationRequested)
            {
                return operationCancellation.DeadlineElapsed
                    ? DeadlineExceeded<FilePanelResult<T>>(revision)
                    : Cancelled<FilePanelResult<T>>(revision);
            }

            HostResult<FilePanelResult<T>>? reservationReplay = null;
            var outcomeReserved = false;
            if (changesState)
            {
                reservationReplay = ReserveReplay<FilePanelResult<T>>(
                    context,
                    fingerprint,
                    revision,
                    out outcomeReserved);
            }

            if (reservationReplay is not null)
            {
                return reservationReplay;
            }

            try
            {
                var fileResult = await operation(filePanel, operationCancellation.Token)
                    .ConfigureAwait(false);
                if (!outcomeReserved && cancellationToken.IsCancellationRequested)
                {
                    return Cancelled<FilePanelResult<T>>(revision);
                }

                if (!outcomeReserved && operationCancellation.DeadlineElapsed)
                {
                    return DeadlineExceeded<FilePanelResult<T>>(revision);
                }

                if (changesState && fileResult.IsSuccess)
                {
                    var engineSnapshot = await filePanel
                        .SnapshotAsync(
                            outcomeReserved
                                ? CancellationToken.None
                                : operationCancellation.Token)
                        .ConfigureAwait(false);
                    if (!session.ApplyEngineSnapshot(engineSnapshot))
                    {
                        session.RecordStateChange($"{operationName} completed.");
                    }
                }

                var resultingRevision = session.Snapshot().Descriptor.Revision;
                var result = HostResult<FilePanelResult<T>>.Succeed(
                    fileResult,
                    resultingRevision);
                if (changesState)
                {
                    CompleteReplay(context, fingerprint, result);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                if (outcomeReserved)
                {
                    return OutcomeUncertain<FilePanelResult<T>>(revision);
                }

                return operationCancellation.DeadlineElapsed
                    ? DeadlineExceeded<FilePanelResult<T>>(revision)
                    : Cancelled<FilePanelResult<T>>(revision);
            }
            catch (Exception exception)
            {
                return outcomeReserved
                    ? OutcomeUncertain<FilePanelResult<T>>(revision)
                    : EngineFailure<FilePanelResult<T>>(exception, revision);
            }
        }
        finally
        {
            if (useIdempotencyGate)
            {
                _idempotentFileOperationGate.Release();
            }
        }
    }

    private bool TryGetFilePanel<T>(
        SessionId sessionId,
        out HostedSession session,
        out IFilePanelSession filePanel,
        out HostResult<T> failure)
    {
        if (!TryGetSession(sessionId, out session))
        {
            filePanel = null!;
            failure = NotFound<T>("session", 0);
            return false;
        }

        var snapshot = session.Snapshot();
        if (snapshot.Descriptor.Lifecycle is SessionLifecycle.Closed or SessionLifecycle.Failed)
        {
            filePanel = null!;
            failure = HostResult<T>.Fail(
                HostError.Create(HostErrorCode.SessionClosed, "The file-panel session is closed."),
                snapshot.Descriptor.Revision);
            return false;
        }

        if (session.Engine is not IFilePanelSession filePanelSession)
        {
            filePanel = null!;
            failure = Unsupported<T>(
                "The requested session does not expose file-panel operations.",
                snapshot.Descriptor.Revision);
            return false;
        }

        filePanel = filePanelSession;
        failure = null!;
        return true;
    }

    private static HostResult<T> DeadlineExceeded<T>(long revision) => HostResult<T>.Fail(
        HostError.Create(
            HostErrorCode.DeadlineExceeded,
            "The operation deadline has elapsed."),
        revision);

    private static string TransferKey(FilePanelTransferRequest request) =>
        $"{LocationKey(request.Source)}:{LocationKey(request.Destination)}:{request.Operation}:"
        + $"{request.ConflictPolicy}";

    private static string PreconditionKey(FilePanelMutationPrecondition precondition) =>
        $"{precondition.Kind}:{precondition.Version ?? string.Empty}";

    private static string LocationKey(FilePanelLocation location)
    {
        var address = location.Address switch
        {
            FilePanelAddress.Hierarchical hierarchical =>
                $"path:{string.Join('/', hierarchical.Path.Segments.Select(item => item.Value))}",
            FilePanelAddress.ObjectKey objectKey => $"object:{objectKey.Key}",
            FilePanelAddress.ContainerRoot => "container-root",
            _ => throw new ArgumentOutOfRangeException(nameof(location), location.Address, null),
        };
        return $"{location.ProviderProfileId}:{location.Authority ?? string.Empty}:{address}:"
            + (location.Version ?? string.Empty);
    }

    private sealed class HostedOperationCancellation : IDisposable
    {
        private readonly CancellationTokenSource? _deadline;
        private readonly CancellationTokenSource? _linked;

        private HostedOperationCancellation(
            CancellationToken token,
            CancellationTokenSource? deadline,
            CancellationTokenSource? linked)
        {
            Token = token;
            _deadline = deadline;
            _linked = linked;
        }

        public CancellationToken Token { get; }

        public bool DeadlineElapsed => _deadline?.IsCancellationRequested == true;

        public static HostedOperationCancellation Create(
            OperationContext context,
            CancellationToken cancellationToken,
            TimeProvider timeProvider)
        {
            if (context.DeadlineUtc is not { } deadlineUtc)
            {
                return new HostedOperationCancellation(cancellationToken, null, null);
            }

            var remaining = deadlineUtc - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                var elapsed = new CancellationTokenSource();
                elapsed.Cancel();
                return new HostedOperationCancellation(elapsed.Token, elapsed, null);
            }

            var deadline = new CancellationTokenSource(remaining, timeProvider);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
            return new HostedOperationCancellation(linked.Token, deadline, linked);
        }

        public void Dispose()
        {
            _linked?.Dispose();
            _deadline?.Dispose();
        }
    }
}
