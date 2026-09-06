using System.Runtime.CompilerServices;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost.Tests;

internal sealed class FakeFilePanelSessionFactory : IFilePanelSessionFactory
{
    private readonly Dictionary<SessionId, FakeFilePanelSession> _sessions = [];

    public CapabilitySet Capabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.FilesList,
        SessionCapabilities.FilesSearch,
        SessionCapabilities.FilesStat,
        SessionCapabilities.FilesPreview,
        SessionCapabilities.FilesReadAccessControl,
        SessionCapabilities.FilesTransfersRead,
        SessionCapabilities.FilesCreateDirectory,
        SessionCapabilities.FilesRename,
        SessionCapabilities.FilesDelete,
        GovernedFileToolNames.SessionWrite,
        GovernedFileToolNames.SessionCopy,
        SessionCapabilities.FilesTransferEnqueue,
        SessionCapabilities.FilesTransferCancel,
        SessionCapabilities.FilesTransferRetry,
    ]);

    public Func<FilePanelLocation, FileSessionMetadata>? MetadataFactory { get; set; }

    public int CreateCount { get; private set; }

    public WorkspaceInstanceId? LastWorkspaceId { get; private set; }

    public Func<FakeFilePanelSession, CancellationToken, ValueTask>? AfterCreateAsync
    {
        get;
        set;
    }

    public Func<CancellationToken, ValueTask>? BeforeSnapshotForNewSessions
    {
        get;
        set;
    }

    public FakeFilePanelSession this[SessionId id] => _sessions[id];

    public async ValueTask<IFilePanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        FilePanelLocation initialLocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateCount++;
        LastWorkspaceId = workspaceId;
        var session = new FakeFilePanelSession(
            sessionId,
            initialLocation,
            Capabilities,
            MetadataFactory?.Invoke(initialLocation))
        {
            BeforeSnapshotAsync = BeforeSnapshotForNewSessions,
        };
        _sessions.Add(sessionId, session);
        if (AfterCreateAsync is { } afterCreate)
        {
            await afterCreate(session, cancellationToken).ConfigureAwait(false);
        }

        return session;
    }
}

internal sealed class FakeFilePanelSession : IFilePanelSession
{
    private readonly List<FilePanelTransferSnapshot> _transfers = [];
    private bool _closed;

    public FakeFilePanelSession(
        SessionId id,
        FilePanelLocation initialLocation,
        CapabilitySet capabilities,
        FileSessionMetadata? metadata = null)
    {
        Id = id;
        InitialLocation = initialLocation;
        Capabilities = capabilities;
        Metadata = metadata ?? new FileSessionMetadata(
            initialLocation,
            FilePanelCapability.List
            | FilePanelCapability.Stat
            | FilePanelCapability.RangedRead,
            maximumListPageSize: 1_000,
            maximumPreviewBytes: 1024 * 1024);
    }

    public SessionId Id { get; }

    public FilePanelLocation InitialLocation { get; }

    public FileSessionMetadata Metadata { get; private set; }

    public PanelKind Kind => PanelKind.FileViewer;

    public CapabilitySet Capabilities { get; private set; }

    public IReadOnlyList<FilePanelTransferSnapshot> Transfers => [.. _transfers];

    public Func<CancellationToken, ValueTask>? BeforeSnapshotAsync { get; set; }

    public int DisposeCount { get; private set; }

    public bool IsClosed => _closed;

    public int CreateDirectoryCount { get; private set; }

    public int DeleteCount { get; private set; }

    public int RenameCount { get; private set; }

    public int WriteTextCount { get; private set; }

    public int CopyCount { get; private set; }

    public int ListCount { get; private set; }

    public int SearchCount { get; private set; }

    public int StatCount { get; private set; }

    public int PreviewCount { get; private set; }

    public FilePanelListRequest? LastListRequest { get; private set; }

    public FilePanelSearchRequest? LastSearchRequest { get; private set; }

    public FilePanelAccessControlRequest? LastAccessControlRequest { get; private set; }

    public FilePanelLocation? LastStatLocation { get; private set; }

    public FilePanelPreviewRequest? LastPreviewRequest { get; private set; }

    public FilePanelCreateDirectoryRequest? LastCreateDirectoryRequest { get; private set; }

    public FilePanelDeleteRequest? LastDeleteRequest { get; private set; }

    public FilePanelRenameRequest? LastRenameRequest { get; private set; }

    public FilePanelTextWriteRequest? LastWriteTextRequest { get; private set; }

    public FilePanelCopyRequest? LastCopyRequest { get; private set; }

    public Func<
        FilePanelListRequest,
        CancellationToken,
        ValueTask<FilePanelResult<FilePanelPage>>>? ListOperation
    { get; set; }

    public Func<
        FilePanelSearchRequest,
        CancellationToken,
        IAsyncEnumerable<FilePanelResult<FilePanelEntry>>>? SearchOperation
    { get; set; }

    public Func<
        FilePanelAccessControlRequest,
        CancellationToken,
        ValueTask<FilePanelResult<FilePanelAccessControl>>>? AccessControlOperation
    { get; set; }

    public Func<
        FilePanelLocation,
        CancellationToken,
        ValueTask<FilePanelResult<FilePanelEntry>>>? StatOperation
    { get; set; }

    public Func<
        FilePanelPreviewRequest,
        CancellationToken,
        ValueTask<FilePanelResult<FilePanelPreview>>>? PreviewOperation
    { get; set; }

    public Func<
        FilePanelCreateDirectoryRequest,
        CancellationToken,
        ValueTask<FilePanelResult<FilePanelEntry>>>? CreateDirectoryOperation
    { get; set; }

    public Func<
        FilePanelDeleteRequest,
        CancellationToken,
        ValueTask<FilePanelResult<FilePanelDeleteReceipt>>>? DeleteOperation
    { get; set; }

    public PanelCloseMode? LastCloseMode { get; private set; }

    public void ReplaceMetadata(FileSessionMetadata metadata) =>
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

    public void RemoveCapability(string capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capability);
        Capabilities = new CapabilitySet(
            Capabilities.Values.Where(item =>
                !string.Equals(item, capability, StringComparison.Ordinal)));
    }

    public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ListCount++;
        LastListRequest = request;
        if (ListOperation is { } operation)
        {
            return operation(request, cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<FilePanelPage>.Success(new FilePanelPage(
            [Entry(request.Location.Child(new FilePanelPathSegment("listed.txt")), "listed.txt")],
            null)));
    }

    public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StatCount++;
        LastStatLocation = location;
        if (StatOperation is { } operation)
        {
            return operation(location, cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<FilePanelEntry>.Success(
            Entry(location, LocationName(location, "stat.txt"))));
    }

    public IAsyncEnumerable<FilePanelResult<FilePanelEntry>> SearchAsync(
        FilePanelSearchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SearchCount++;
        LastSearchRequest = request;
        return SearchOperation is { } operation
            ? operation(request, cancellationToken)
            : DefaultSearchAsync(request, cancellationToken);
    }

    public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PreviewCount++;
        LastPreviewRequest = request;
        if (PreviewOperation is { } operation)
        {
            return operation(request, cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<FilePanelPreview>.Success(new FilePanelPreview(
            request.Location,
            FilePanelPreviewKind.Text,
            "text/plain",
            "preview"u8,
            false)));
    }

    public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateDirectoryCount++;
        LastCreateDirectoryRequest = request;
        if (CreateDirectoryOperation is { } operation)
        {
            return operation(request, cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<FilePanelEntry>.Success(new FilePanelEntry(
            request.Location,
            request.Location.Address is FilePanelAddress.Hierarchical hierarchical
                ? hierarchical.Path.Name?.Value ?? "directory"
                : "directory",
            FilePanelEntryKind.Directory,
            null,
            null,
            false)));
    }

    public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RenameCount++;
        LastRenameRequest = request;
        return ValueTask.FromResult(FilePanelResult<FilePanelEntry>.Success(Entry(
            request.Destination,
            request.Destination.Address is FilePanelAddress.Hierarchical hierarchical
                ? hierarchical.Path.Name?.Value ?? "renamed"
                : "renamed")));
    }

    public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteCount++;
        LastDeleteRequest = request;
        if (DeleteOperation is { } operation)
        {
            return operation(request, cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<FilePanelDeleteReceipt>.Success(
            new FilePanelDeleteReceipt(request.Location, false)));
    }

    public ValueTask<FilePanelResult<FilePanelTextWriteReceipt>> WriteTextAsync(
        FilePanelTextWriteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteTextCount++;
        LastWriteTextRequest = request;
        var replaced = request.Precondition.Kind ==
            FilePanelMutationPreconditionKind.VersionMatches;
        return ValueTask.FromResult(FilePanelResult<FilePanelTextWriteReceipt>.Success(
            new FilePanelTextWriteReceipt(
                request.Location.WithVersion("written-version"),
                Encoding.UTF8.GetByteCount(request.Content),
                replaced)));
    }

    public ValueTask<FilePanelResult<FilePanelCopyReceipt>> CopyAsync(
        FilePanelCopyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CopyCount++;
        LastCopyRequest = request;
        return ValueTask.FromResult(FilePanelResult<FilePanelCopyReceipt>.Success(
            new FilePanelCopyReceipt(
                request.Source,
                request.Destination.WithVersion("copied-version"),
                bytesCopied: 7)));
    }

    public ValueTask<FilePanelResult<FilePanelAccessControl>> GetAccessControlAsync(
        FilePanelAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastAccessControlRequest = request;
        if (AccessControlOperation is { } operation)
        {
            return operation(request, cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<FilePanelAccessControl>.Success(
            new FilePanelAccessControl(
                request.Location,
                mode: new FilePanelPosixMode(0x1A4),
                owner: "owner",
                group: "group")));
    }

    public void AddTransfer(FilePanelTransferSnapshot snapshot) =>
        _transfers.Add(snapshot ?? throw new ArgumentNullException(nameof(snapshot)));

    public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueTransferAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = TransferSnapshot(request);
        _transfers.Add(snapshot);
        return ValueTask.FromResult(FilePanelResult<FilePanelTransferSnapshot>.Success(snapshot));
    }

    public ValueTask<FilePanelResult<Unit>> CancelTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _transfers.FindIndex(item => item.Id == id);
        _transfers[index] = _transfers[index] with
        {
            State = FilePanelTransferState.Cancelled,
            Stage = "Cancelled",
            CompletedAt = DateTimeOffset.UtcNow,
        };
        return ValueTask.FromResult(FilePanelResult<Unit>.Success(Unit.Value));
    }

    public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = _transfers.Single(item => item.Id == id).Request;
        var snapshot = TransferSnapshot(request);
        _transfers.Add(snapshot);
        return ValueTask.FromResult(FilePanelResult<FilePanelTransferSnapshot>.Success(snapshot));
    }

    public async ValueTask<PanelSessionSnapshot> SnapshotAsync(
        CancellationToken cancellationToken)
    {
        if (BeforeSnapshotAsync is { } beforeSnapshot)
        {
            await beforeSnapshot(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var active = _transfers.Count(item => item.CanCancel);
        return _closed
            ? new PanelSessionSnapshot(
                SessionLifecycle.Closed,
                SessionHealth.Ended,
                false,
                "Closed")
            : new PanelSessionSnapshot(
                SessionLifecycle.Active,
                SessionHealth.Healthy,
                active > 0,
                active > 0 ? $"{active} active transfer(s)." : "Ready");
    }

    public async IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _ = afterSequence;
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_closed)
        {
            return ValueTask.FromResult(PanelCloseOutcome.AlreadyClosed);
        }

        LastCloseMode = mode;
        if (mode == PanelCloseMode.Graceful && _transfers.Any(item => item.CanCancel))
        {
            return ValueTask.FromResult(PanelCloseOutcome.ConfirmationRequired);
        }

        if (mode == PanelCloseMode.Force)
        {
            for (var index = 0; index < _transfers.Count; index++)
            {
                if (_transfers[index].CanCancel)
                {
                    _transfers[index] = _transfers[index] with
                    {
                        State = FilePanelTransferState.Cancelled,
                        Stage = "Cancelled",
                        CompletedAt = DateTimeOffset.UtcNow,
                    };
                }
            }
        }

        _closed = true;
        return ValueTask.FromResult(mode == PanelCloseMode.Force
            ? PanelCloseOutcome.ForceTerminated
            : PanelCloseOutcome.GracefullyClosed);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        _closed = true;
        return ValueTask.CompletedTask;
    }

    private static FilePanelEntry Entry(FilePanelLocation location, string name) => new(
        location.WithVersion("test-version"),
        name,
        FilePanelEntryKind.File,
        7,
        null,
        false);

    private static async IAsyncEnumerable<FilePanelResult<FilePanelEntry>>
        DefaultSearchAsync(
            FilePanelSearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var location = request.Location.Child(
            new FilePanelPathSegment($"{request.Query}.txt"));
        yield return FilePanelResult<FilePanelEntry>.Success(
            Entry(location, $"{request.Query}.txt"));
        await Task.CompletedTask;
    }

    private static string LocationName(
        FilePanelLocation location,
        string fallback) =>
        location.Address is FilePanelAddress.Hierarchical hierarchical
            ? hierarchical.Path.Name?.Value ?? fallback
            : fallback;

    private static FilePanelTransferSnapshot TransferSnapshot(FilePanelTransferRequest request) => new(
        FilePanelTransferId.New(),
        request,
        request.Destination,
        FilePanelTransferState.Running,
        "Running",
        0,
        null,
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null);
}
