using Asura.Application;
using Asura.Core;

namespace Asura.App;

/// <summary>
/// Adapts one owner-scoped file-panel session to the presentation-facing file APIs. Provider
/// errors cross this boundary unchanged; only host transport and lifecycle failures require a
/// compatibility mapping because <see cref="IFilePanelClient"/> cannot represent them directly.
/// </summary>
public sealed class SessionHostedFilePanelClient :
    IFilePanelClient,
    IFileTransferQueueClient,
    IHostedFilePanelClient,
    IFileContentSource,
    IDisposable
{
    private readonly object _profileGate = new();
    private readonly object _transferGate = new();
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly ISessionHostClient _sessionHost;
    private readonly IFilePanelClient _profileSource;
    private readonly IFileProviderProfileRuntime? _profileRuntime;
    private readonly IFileTransferQueueClient? _transferProjection;
    private readonly HostedFilePanelClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<FilePanelTransferId> _ownedTransferIds = [];
    private readonly Dictionary<FilePanelTransferId, FilePanelTransferSnapshot> _knownTransfers = [];
    private IReadOnlyList<FileProviderProfileDescriptor>? _boundProfiles;
    private SessionSnapshot? _initialSnapshot;
    private FilePanelLocation? _bindingLocation;
    private long _revision = -1;
    private bool _closed;
    private bool _disposed;

    public SessionHostedFilePanelClient(
        ISessionHostClient sessionHost,
        IFilePanelClient profileSource,
        HostedFilePanelClientOptions options,
        IFileTransferQueueClient? transferProjection = null,
        TimeProvider? timeProvider = null)
    {
        _sessionHost = sessionHost ?? throw new ArgumentNullException(nameof(sessionHost));
        _profileSource = profileSource ?? throw new ArgumentNullException(nameof(profileSource));
        _profileRuntime = profileSource as IFileProviderProfileRuntime;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transferProjection = transferProjection;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transferProjection?.TransfersChanged += OnProjectedTransfersChanged;

        _profileRuntime?.ProfilesChanged += OnSourceProfilesChanged;
    }

    public event EventHandler? ProfilesChanged;

    public event EventHandler? TransfersChanged;

    public SessionId SessionId => _options.SessionId;

    public SessionOwner Owner => _options.Owner;

    public ClientId ClientId => _options.ClientId;

    public bool IsInitialized => Volatile.Read(ref _initialSnapshot) is not null;

    public long? Revision
    {
        get
        {
            var revision = Interlocked.Read(ref _revision);
            return revision < 0 ? null : revision;
        }
    }

    /// <summary>
    /// Whole-file content is not a session operation: it is served from this
    /// machine's memory and cache, so it goes straight to the provider client
    /// rather than through the session host. A client that cannot serve it
    /// refuses here, exactly as it would if the panel had asked it directly.
    /// </summary>
    public ValueTask<FilePanelResult<FilePreviewContent>> OpenContentAsync(
        FilePanelLocation location,
        long maximumBytes,
        CancellationToken cancellationToken) =>
        _profileSource is IFileContentSource source
            ? source.OpenContentAsync(location, maximumBytes, cancellationToken)
            : ValueTask.FromResult(FilePanelResult<FilePreviewContent>.Failure(
                new FilePanelError(
                    FilePanelErrorCode.UnsupportedCapability,
                    "file_content_unsupported",
                    "This file client cannot open whole files.",
                    false)));

    public IReadOnlyList<FileProviderProfileDescriptor> Profiles
    {
        get
        {
            lock (_profileGate)
            {
                return _boundProfiles ?? _profileSource.Profiles;
            }
        }
    }

    public IReadOnlyList<FilePanelTransferSnapshot> Transfers
    {
        get
        {
            lock (_transferGate)
            {
                var transfers = new Dictionary<
                    FilePanelTransferId,
                    FilePanelTransferSnapshot>(_knownTransfers);
                if (_transferProjection is not null)
                {
                    foreach (var transfer in _transferProjection.Transfers)
                    {
                        if (_ownedTransferIds.Contains(transfer.Id))
                        {
                            transfers[transfer.Id] = transfer;
                        }
                    }
                }

                return Array.AsReadOnly(transfers.Values
                    .OrderByDescending(transfer => transfer.QueuedAt)
                    .ToArray());
            }
        }
    }

    public IAsyncEnumerable<FilePanelResult<FilePanelEntry>> SearchAsync(
        FilePanelSearchRequest request,
        CancellationToken cancellationToken) =>
        FilePanelSearch.FindAsync(this, request, cancellationToken);

    public IAsyncEnumerable<FilePanelResult<FilePanelChange>> WatchAsync(
        FilePanelWatchRequest request,
        CancellationToken cancellationToken) =>
        FilePanelWatch.ObserveAsync(this, request, cancellationToken);

    public async ValueTask<HostResult<SessionSnapshot>> InitializeAsync(
        CancellationToken cancellationToken) =>
        await InitializeAsync(_options.InitialLocation, cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<HostResult<SessionSnapshot>> InitializeAsync(
        FilePanelLocation? requestedInitialLocation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialSnapshot is { } initialized)
        {
            return HostResult<SessionSnapshot>.Succeed(
                initialized,
                Revision ?? initialized.Descriptor.Revision);
        }

        try
        {
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<SessionSnapshot>();
        }

        try
        {
            if (_initialSnapshot is { } existing)
            {
                return HostResult<SessionSnapshot>.Succeed(
                    existing,
                    Revision ?? existing.Descriptor.Revision);
            }

            if (_closed)
            {
                return HostResult<SessionSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.SessionClosed,
                        "The file-panel session has already been closed."),
                    Revision ?? 0);
            }

            var initialLocation = _options.InitialLocation ?? requestedInitialLocation;
            if (initialLocation is null)
            {
                return HostResult<SessionSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.InvalidRequest,
                        "A deferred file-panel session must bind from its first file location."),
                    Revision ?? 0);
            }

            if (_options.RequiredProfileId is { } requiredProfileId
                && !string.Equals(initialLocation.ProviderProfileId, requiredProfileId.Value, StringComparison.Ordinal))
            {
                return HostResult<SessionSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.InvalidRequest,
                        "The first file location does not belong to the saved provider profile."),
                    Revision ?? 0);
            }

            if (!_profileSource.Profiles.Any(profile => string.Equals(profile.Id, initialLocation.ProviderProfileId, StringComparison.Ordinal)))
            {
                return HostResult<SessionSnapshot>.Fail(
                    HostError.Create(
                        HostErrorCode.NotFound,
                        "The file-provider profile required by this panel is not available."),
                    Revision ?? 0);
            }

            var profilesBeforeBinding = _profileSource.Profiles.ToArray();
            var result = await _sessionHost.EnsureFilePanelSessionAsync(
                    new EnsureFilePanelSessionRequest(
                        SessionId,
                        Owner,
                        _options.Title,
                        initialLocation),
                    NewContext(expectedRevision: null, isMutation: true),
                    cancellationToken)
                .ConfigureAwait(false);
            ObserveRevision(result);
            if (result is HostResult<SessionSnapshot>.Success success)
            {
                _bindingLocation = initialLocation;
                _initialSnapshot = success.Value;
                FreezeProfiles(
                    profilesBeforeBinding,
                    success.Value.Descriptor.FileMetadata);
            }

            return result;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            (context, token) => _sessionHost.ListFilesAsync(
                new FilePanelListHostRequest(SessionId, request),
                context,
                token),
            request.Location,
            isMutation: false,
            cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        return ExecuteAsync(
            (context, token) => _sessionHost.StatFileAsync(
                new FilePanelStatHostRequest(SessionId, location),
                context,
                token),
            location,
            isMutation: false,
            cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            (context, token) => _sessionHost.PreviewFileAsync(
                new FilePanelPreviewHostRequest(SessionId, request),
                context,
                token),
            request.Location,
            isMutation: false,
            cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            (context, token) => _sessionHost.CreateFileDirectoryAsync(
                new FilePanelCreateDirectoryHostRequest(SessionId, request),
                context,
                token),
            request.Location,
            isMutation: true,
            cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            (context, token) => _sessionHost.RenameFileAsync(
                new FilePanelRenameHostRequest(SessionId, request),
                context,
                token),
            request.Source,
            isMutation: true,
            cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelAccessControl>> GetAccessControlAsync(
        FilePanelAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            (context, token) => _sessionHost.GetFileAccessControlAsync(
                new FilePanelAccessControlHostRequest(SessionId, request),
                context,
                token),
            request.Location,
            isMutation: false,
            cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelAccessControl>> SetAccessControlAsync(
        FilePanelSetAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            (context, token) => _sessionHost.SetFileAccessControlAsync(
                new FilePanelSetAccessControlHostRequest(SessionId, request),
                context,
                token),
            request.Location,
            isMutation: true,
            cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            (context, token) => _sessionHost.DeleteFileAsync(
                new FilePanelDeleteHostRequest(SessionId, request),
                context,
                token),
            request.Location,
            isMutation: true,
            cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelTextWriteReceipt>> WriteTextAsync(
        FilePanelTextWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(FilePanelResult<FilePanelTextWriteReceipt>.Failure(
            FilePanelMutationErrors.Unsupported));
    }

    public ValueTask<FilePanelResult<FilePanelCopyReceipt>> CopyAsync(
        FilePanelCopyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(FilePanelResult<FilePanelCopyReceipt>.Failure(
            FilePanelMutationErrors.Unsupported));
    }

    public async ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ExecuteAsync(
                (context, token) => _sessionHost.EnqueueFileTransferAsync(
                    new FilePanelTransferEnqueueHostRequest(SessionId, request),
                    context,
                    token),
                ResolveTransferBindingLocation(request),
                isMutation: true,
                cancellationToken)
            .ConfigureAwait(false);
        RecordOwnedTransfer(result);
        return result;
    }

    public async ValueTask<FilePanelResult<Unit>> CancelAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
                (context, token) => _sessionHost.CancelFileTransferAsync(
                    new FilePanelTransferCancelHostRequest(SessionId, id),
                    context,
                    token),
                requestedInitialLocation: null,
                isMutation: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess)
        {
            TransfersChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public async ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
                (context, token) => _sessionHost.RetryFileTransferAsync(
                    new FilePanelTransferRetryHostRequest(SessionId, id),
                    context,
                    token),
                requestedInitialLocation: null,
                isMutation: true,
                cancellationToken)
            .ConfigureAwait(false);
        RecordOwnedTransfer(result);
        return result;
    }

    public async ValueTask<HostResult<CloseScopeResult>> CloseAsync(
        CloseDecision decision,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, null);
        }

        if (_options.InitialLocation is null && _initialSnapshot is null)
        {
            var unbound = await CloseUnboundDeferredSessionAsync(cancellationToken)
                .ConfigureAwait(false);
            if (unbound is not null)
            {
                return unbound;
            }
        }

        var initialization = await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (initialization is HostResult<SessionSnapshot>.Failure failure)
        {
            return HostResult<CloseScopeResult>.Fail(
                failure.Error,
                failure.CurrentRevision);
        }

        var revision = Revision ?? 0;
        var request = new CloseScopeRequest(
            CloseScopeKind.Session,
            SessionId.Value,
            decision,
            new Dictionary<SessionId, long> { [SessionId] = revision });
        var result = await _sessionHost.CloseAsync(
                request,
                NewContext(expectedRevision: null, isMutation: true),
                cancellationToken)
            .ConfigureAwait(false);
        ObserveRevision(result);
        if (result is HostResult<CloseScopeResult>.Success { Value: CloseScopeResult.Completed completed }
            && completed.Sessions.Any(IsClosedSessionResult))
        {
            _closed = true;
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transferProjection?.TransfersChanged -= OnProjectedTransfersChanged;

        _profileRuntime?.ProfilesChanged -= OnSourceProfilesChanged;
    }

    private async ValueTask<FilePanelResult<T>> ExecuteAsync<T>(
        Func<
            OperationContext,
            CancellationToken,
            ValueTask<HostResult<FilePanelResult<T>>>> operation,
        FilePanelLocation? requestedInitialLocation,
        bool isMutation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var initialization = await InitializeAsync(
                requestedInitialLocation,
                cancellationToken)
            .ConfigureAwait(false);
        if (initialization is HostResult<SessionSnapshot>.Failure initializationFailure)
        {
            return HostFailure<T>(initializationFailure.Error);
        }

        var result = await operation(
                NewContext(Revision, isMutation),
                cancellationToken)
            .ConfigureAwait(false);
        ObserveRevision(result);
        return result switch
        {
            HostResult<FilePanelResult<T>>.Success success => success.Value,
            HostResult<FilePanelResult<T>>.Failure failure => HostFailure<T>(failure.Error),
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
    }

    private async ValueTask<HostResult<CloseScopeResult>?>
        CloseUnboundDeferredSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled<CloseScopeResult>();
        }

        try
        {
            if (_initialSnapshot is not null)
            {
                return null;
            }

            _closed = true;
            var completed = new CloseScopeResult.Completed(
                CloseScopeKind.Session,
                SessionId.Value,
                [
                    new SessionCloseResult(
                        SessionId,
                        SessionCloseOutcome.AlreadyClosed,
                        "No hosted file session was created."),
                ]);
            return HostResult<CloseScopeResult>.Succeed(completed, Revision ?? 0);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private FilePanelLocation? ResolveTransferBindingLocation(
        FilePanelTransferRequest request)
    {
        if (_options.RequiredProfileId is not { } requiredProfileId)
        {
            return request.Source;
        }

        if (string.Equals(request.Source.ProviderProfileId, requiredProfileId.Value, StringComparison.Ordinal))
        {
            return request.Source;
        }

        return string.Equals(request.Destination.ProviderProfileId, requiredProfileId.Value
, StringComparison.Ordinal) ? request.Destination
            : request.Source;
    }

    private OperationContext NewContext(long? expectedRevision, bool isMutation) =>
        OperationContext.ForHuman(
            ClientId,
            expectedRevision,
            isMutation ? IdempotencyKey.New() : null,
            _timeProvider.GetUtcNow().Add(_options.OperationTimeout));

    private void ObserveRevision<T>(HostResult<T> result)
    {
        var revision = result switch
        {
            HostResult<T>.Success success => success.ResultingRevision,
            HostResult<T>.Failure failure => failure.CurrentRevision,
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
        long current;
        do
        {
            current = Interlocked.Read(ref _revision);
            if (revision <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _revision, revision, current) != current);
    }

    private void RecordOwnedTransfer(
        FilePanelResult<FilePanelTransferSnapshot> result)
    {
        if (!result.IsSuccess)
        {
            return;
        }

        var transfer = result.Value!;
        lock (_transferGate)
        {
            _ownedTransferIds.Add(transfer.Id);
            _knownTransfers[transfer.Id] = transfer;
        }

        TransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnProjectedTransfersChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        TransfersChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSourceProfilesChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        lock (_profileGate)
        {
            if (_boundProfiles is not null)
            {
                return;
            }
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FreezeProfiles(
        IReadOnlyList<FileProviderProfileDescriptor> profilesBeforeBinding,
        FileSessionMetadata? metadata)
    {
        var profilesAfterBinding = _profileSource.Profiles.ToArray();
        IReadOnlyList<FileProviderProfileDescriptor> boundProfiles;
        if (profilesBeforeBinding.SequenceEqual(profilesAfterBinding))
        {
            boundProfiles = Array.AsReadOnly(profilesAfterBinding);
        }
        else
        {
            var profileId = metadata?.TrustedRoot.ProviderProfileId
                ?? _bindingLocation?.ProviderProfileId
                ?? throw new InvalidOperationException(
                    "A hosted file session cannot freeze profiles before binding.");
            var initialProfile = profilesAfterBinding
                .Concat(profilesBeforeBinding)
                .FirstOrDefault(profile =>
                    string.Equals(profile.Id, profileId, StringComparison.Ordinal));
            boundProfiles = initialProfile is null || metadata is null
                ? Array.AsReadOnly(profilesBeforeBinding.ToArray())
                :
                [
                    new FileProviderProfileDescriptor(
                        initialProfile.Id,
                        initialProfile.Name,
                        initialProfile.Family,
                        metadata.TrustedRoot,
                        metadata.Capabilities,
                        metadata.MaximumListPageSize,
                        metadata.MaximumPreviewBytes,
                        RequiresHostTransferForPreview:
                            initialProfile.RequiresHostTransferForPreview),
                ];
        }

        lock (_profileGate)
        {
            if (_boundProfiles is not null)
            {
                return;
            }

            _boundProfiles = boundProfiles;
        }

        _profileRuntime?.ProfilesChanged -= OnSourceProfilesChanged;

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsClosedSessionResult(SessionCloseResult result) =>
        result.SessionId == SessionId
        && result.Outcome is SessionCloseOutcome.GracefullyClosed
            or SessionCloseOutcome.ForceTerminated
            or SessionCloseOutcome.AlreadyClosed;

    private HostResult<T> Cancelled<T>() => HostResult<T>.Fail(
        HostError.Create(HostErrorCode.Cancelled, "The operation was cancelled."),
        Revision ?? 0);

    private static FilePanelResult<T> HostFailure<T>(HostError error) =>
        FilePanelResult<T>.Failure(new FilePanelError(
            MapHostErrorCode(error.Code),
            $"host_{error.StableCode}",
            error.Message,
            error.Retryable));

    private static FilePanelErrorCode MapHostErrorCode(HostErrorCode code) => code switch
    {
        HostErrorCode.Cancelled => FilePanelErrorCode.Cancelled,
        HostErrorCode.RevisionConflict or HostErrorCode.IdempotencyKeyReused =>
            FilePanelErrorCode.Conflict,
        HostErrorCode.CapabilityNotSupported => FilePanelErrorCode.UnsupportedCapability,
        HostErrorCode.LeaseDenied or HostErrorCode.ConfirmationRequired =>
            FilePanelErrorCode.AccessDenied,
        HostErrorCode.NotFound
            or HostErrorCode.SessionClosed
            or HostErrorCode.ResynchronizationRequired => FilePanelErrorCode.Offline,
        HostErrorCode.InvalidRequest => FilePanelErrorCode.InvalidLocation,
        HostErrorCode.UnsupportedProtocol
            or HostErrorCode.DeadlineExceeded
            or HostErrorCode.EngineFailed => FilePanelErrorCode.IoFailure,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}
