using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.SessionHost.Tests;

public sealed class FilePanelOperationTests
{
    [Fact]
    public async Task FileSessionCreationPreservesOwningWorkspaceIdentity()
    {
        var files = new FakeFilePanelSessionFactory();
        await using var host = CreateHost(
            files,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var request = OpenRequest("files-workspace-route");

        _ = await host.EnsureFilePanelSessionAsync(
            request,
            Context(),
            CancellationToken.None);

        Assert.Equal(request.Owner.WorkspaceId, files.LastWorkspaceId);
    }

    [Fact]
    public async Task CancellationDuringFileSessionCreationRetainsUncertainReplay()
    {
        var files = new FakeFilePanelSessionFactory();
        var creationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        files.AfterCreateAsync = async (_, cancellationToken) =>
        {
            creationEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                .ConfigureAwait(false);
        };
        await using var host = CreateHost(
            files,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var request = OpenRequest("files-create-cancelled");
        var context = Context(
            idempotencyKey: new IdempotencyKey("files-create-cancelled"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.EnsureFilePanelSessionAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await creationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var uncertain = await pending;
        var replay = await host.EnsureFilePanelSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, files.CreateCount);
    }

    [Fact]
    public async Task FileSessionSnapshotFailureDisposesEngineAndRetainsUncertainReplay()
    {
        var files = new FakeFilePanelSessionFactory
        {
            BeforeSnapshotForNewSessions = static _ =>
                ValueTask.FromException(new IOException("fake snapshot failure")),
        };
        await using var host = CreateHost(
            files,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var request = OpenRequest("files-create-failed");
        var context = Context(
            idempotencyKey: new IdempotencyKey("files-create-failed"));

        var uncertain = await host.EnsureFilePanelSessionAsync(
            request,
            context,
            CancellationToken.None);
        var replay = await host.EnsureFilePanelSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, files[request.SessionId].DisposeCount);
        Assert.Equal(1, files.CreateCount);
    }

    [Fact]
    public async Task ConcurrentFileCreationCompletesKnownSuccessAfterCallerCancellation()
    {
        var files = new FakeFilePanelSessionFactory();
        var snapshotEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotToken = CancellationToken.None;
        files.BeforeSnapshotForNewSessions = async cancellationToken =>
        {
            snapshotToken = cancellationToken;
            snapshotEntered.TrySetResult();
            await releaseSnapshot.Task.ConfigureAwait(false);
        };
        await using var host = CreateHost(
            files,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var request = OpenRequest("files-create-known");
        var context = Context(
            idempotencyKey: new IdempotencyKey("files-create-known"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.EnsureFilePanelSessionAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await snapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var concurrentReplay = await host.EnsureFilePanelSessionAsync(
            request,
            context,
            CancellationToken.None);
        releaseSnapshot.TrySetResult();

        var completed = await pending;
        var completedReplay = await host.EnsureFilePanelSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            concurrentReplay.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(completed);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(completedReplay);
        Assert.False(snapshotToken.CanBeCanceled);
        Assert.Equal(1, files.CreateCount);
    }

    [Fact]
    public async Task CancellationBeforeFileSessionCreationLeavesKeyFresh()
    {
        var files = new FakeFilePanelSessionFactory();
        await using var host = CreateHost(
            files,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var request = OpenRequest("files-create-pre-cancelled");
        var context = Context(
            idempotencyKey: new IdempotencyKey("files-create-pre-cancelled"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await host.EnsureFilePanelSessionAsync(
            request,
            context,
            cancellation.Token);
        var retry = await host.EnsureFilePanelSessionAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(retry);
        Assert.Equal(1, files.CreateCount);
    }

    [Fact]
    public async Task FileOpenReservationRejectsCrossFamilyTerminalOpen()
    {
        var files = new FakeFilePanelSessionFactory();
        var terminals = new FakeTerminalSessionFactory();
        var snapshotEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeSnapshotForNewSessions = async _ =>
        {
            snapshotEntered.TrySetResult();
            await releaseSnapshot.Task.ConfigureAwait(false);
        };
        await using var host = CreateHost(
            files,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch),
            terminals);
        var context = Context(
            idempotencyKey: new IdempotencyKey("file-cross-family"));

        var file = host.EnsureFilePanelSessionAsync(
            OpenRequest("file-cross-family"),
            context,
            CancellationToken.None).AsTask();
        await snapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rejected = await host.EnsureTerminalSessionAsync(
            new EnsureTerminalSessionRequest(
                new SessionId("file-cross-family-terminal"),
                Owner("file-cross-family-terminal-panel"),
                "Terminal",
                new TerminalLaunchRequest("/tmp")),
            context,
            CancellationToken.None);
        releaseSnapshot.TrySetResult();

        Assert.Equal(HostErrorCode.IdempotencyKeyReused, rejected.Error().Code);
        Assert.IsType<HostResult<SessionSnapshot>.Success>(await file);
        Assert.Equal(1, files.CreateCount);
        Assert.Equal(0, terminals.CreateCount);
    }

    [Fact]
    public async Task HostDispatchesEveryFileOperationAndAdvancesMutationRevision()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var files = new FakeFilePanelSessionFactory();
        await using var host = CreateHost(files, clock);
        var sessionId = new SessionId("files-1");
        var root = Root();
        var opened = Assert.IsType<HostResult<SessionSnapshot>.Success>(
            await host.EnsureFilePanelSessionAsync(
                new EnsureFilePanelSessionRequest(
                    sessionId,
                    Owner("panel-1"),
                    "Files",
                    root),
                Context(),
                CancellationToken.None));

        var hello = (await host.NegotiateAsync(
            new ClientHello([1], AllFileCapabilities()),
            Context(),
            CancellationToken.None)).Value();
        Assert.True(hello.Capabilities.Contains(SessionCapabilities.FilesTransferRetry));

        var list = (await host.ListFilesAsync(
            new FilePanelListHostRequest(
                sessionId,
                new FilePanelListRequest(root, 20, null, ShowHidden: false)),
            Context(expectedRevision: opened.ResultingRevision),
            CancellationToken.None)).Value();
        var stat = (await host.StatFileAsync(
            new FilePanelStatHostRequest(sessionId, Child(root, "listed.txt")),
            Context(expectedRevision: opened.ResultingRevision),
            CancellationToken.None)).Value();
        var preview = (await host.PreviewFileAsync(
            new FilePanelPreviewHostRequest(
                sessionId,
                new FilePanelPreviewRequest(Child(root, "listed.txt"), 128)),
            Context(expectedRevision: opened.ResultingRevision),
            CancellationToken.None)).Value();

        Assert.True(list.IsSuccess, list.Error?.Message);
        Assert.True(stat.IsSuccess, stat.Error?.Message);
        Assert.True(preview.IsSuccess, preview.Error?.Message);

        var directoryRequest = new FilePanelCreateDirectoryHostRequest(
            sessionId,
            new FilePanelCreateDirectoryRequest(
                Child(root, "new-directory"),
                FilePanelMutationPrecondition.MustNotExist));
        var idempotency = new IdempotencyKey("mkdir-1");
        var createContext = Context(opened.ResultingRevision, idempotency);
        var created = Assert.IsType<HostResult<FilePanelResult<FilePanelEntry>>.Success>(
            await host.CreateFileDirectoryAsync(
                directoryRequest,
                createContext,
                CancellationToken.None));
        var replayed = Assert.IsType<HostResult<FilePanelResult<FilePanelEntry>>.Success>(
            await host.CreateFileDirectoryAsync(
                directoryRequest,
                createContext,
                CancellationToken.None));

        Assert.True(created.Value.IsSuccess, created.Value.Error?.Message);
        Assert.Equal(created.ResultingRevision, replayed.ResultingRevision);
        Assert.Equal(1, files[sessionId].CreateDirectoryCount);
        Assert.True(created.ResultingRevision > opened.ResultingRevision);

        var renamed = Assert.IsType<HostResult<FilePanelResult<FilePanelEntry>>.Success>(
            await host.RenameFileAsync(
                new FilePanelRenameHostRequest(
                    sessionId,
                    new FilePanelRenameRequest(
                        Child(root, "source.txt"),
                        Child(root, "renamed.txt"),
                        FilePanelMutationPrecondition.MustNotExist)),
                Context(created.ResultingRevision),
                CancellationToken.None));
        var deleted = Assert.IsType<HostResult<FilePanelResult<FilePanelDeleteReceipt>>.Success>(
            await host.DeleteFileAsync(
                new FilePanelDeleteHostRequest(
                    sessionId,
                    new FilePanelDeleteRequest(
                        Child(root, "renamed.txt"),
                        Recursive: false,
                        FilePanelMutationPrecondition.MustExist)),
                Context(renamed.ResultingRevision),
                CancellationToken.None));

        var transferRequest = Transfer(root, "source.bin", "destination.bin");
        var enqueued = Assert.IsType<
            HostResult<FilePanelResult<FilePanelTransferSnapshot>>.Success>(
            await host.EnqueueFileTransferAsync(
                new FilePanelTransferEnqueueHostRequest(sessionId, transferRequest),
                Context(deleted.ResultingRevision),
                CancellationToken.None));
        var cancelled = Assert.IsType<HostResult<FilePanelResult<Unit>>.Success>(
            await host.CancelFileTransferAsync(
                new FilePanelTransferCancelHostRequest(sessionId, enqueued.Value.Value!.Id),
                Context(enqueued.ResultingRevision),
                CancellationToken.None));
        var retried = Assert.IsType<
            HostResult<FilePanelResult<FilePanelTransferSnapshot>>.Success>(
            await host.RetryFileTransferAsync(
                new FilePanelTransferRetryHostRequest(sessionId, enqueued.Value.Value.Id),
                Context(cancelled.ResultingRevision),
                CancellationToken.None));

        Assert.True(renamed.Value.IsSuccess, renamed.Value.Error?.Message);
        Assert.True(deleted.Value.IsSuccess, deleted.Value.Error?.Message);
        Assert.True(enqueued.Value.IsSuccess, enqueued.Value.Error?.Message);
        Assert.True(cancelled.Value.IsSuccess, cancelled.Value.Error?.Message);
        Assert.True(retried.Value.IsSuccess, retried.Value.Error?.Message);
        Assert.NotEqual(enqueued.Value.Value.Id, retried.Value.Value!.Id);
    }

    [Fact]
    public async Task CancellationAfterFileDispatchLeavesAnUncertainReplay()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var files = new FakeFilePanelSessionFactory();
        await using var host = CreateHost(files, clock);
        var sessionId = new SessionId("files-1");
        var root = Root();
        var opened = await OpenAsync(
            host,
            sessionId,
            Owner("panel-1"),
            root);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        files[sessionId].CreateDirectoryOperation = async (request, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return SuccessfulDirectory(request);
        };
        var request = new FilePanelCreateDirectoryHostRequest(
            sessionId,
            new FilePanelCreateDirectoryRequest(
                Child(root, "new-directory"),
                FilePanelMutationPrecondition.MustNotExist));
        var context = Context(
            opened.Descriptor.Revision,
            new IdempotencyKey("cancelled-after-file-dispatch"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.CreateFileDirectoryAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var uncertain = await pending;
        var replay = await host.CreateFileDirectoryAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, files[sessionId].CreateDirectoryCount);
    }

    [Fact]
    public async Task CancellationBeforeFileDispatchDoesNotReserveTheKey()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var files = new FakeFilePanelSessionFactory();
        await using var host = CreateHost(files, clock);
        var sessionId = new SessionId("files-1");
        var root = Root();
        var opened = await OpenAsync(
            host,
            sessionId,
            Owner("panel-1"),
            root);
        var request = new FilePanelCreateDirectoryHostRequest(
            sessionId,
            new FilePanelCreateDirectoryRequest(
                Child(root, "new-directory"),
                FilePanelMutationPrecondition.MustNotExist));
        var context = Context(
            opened.Descriptor.Revision,
            new IdempotencyKey("cancelled-before-file-dispatch"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var cancelled = await host.CreateFileDirectoryAsync(
            request,
            context,
            cancellation.Token);
        var retry = await host.CreateFileDirectoryAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
        Assert.IsType<HostResult<FilePanelResult<FilePanelEntry>>.Success>(retry);
        Assert.Equal(1, files[sessionId].CreateDirectoryCount);
    }

    [Fact]
    public async Task KnownFileResultCompletesReplayAfterCallerCancellation()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var files = new FakeFilePanelSessionFactory();
        await using var host = CreateHost(files, clock);
        var sessionId = new SessionId("files-1");
        var root = Root();
        var opened = await OpenAsync(
            host,
            sessionId,
            Owner("panel-1"),
            root);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        files[sessionId].CreateDirectoryOperation = async (request, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return SuccessfulDirectory(request);
        };
        var request = new FilePanelCreateDirectoryHostRequest(
            sessionId,
            new FilePanelCreateDirectoryRequest(
                Child(root, "new-directory"),
                FilePanelMutationPrecondition.MustNotExist));
        var context = Context(
            opened.Descriptor.Revision,
            new IdempotencyKey("known-after-file-cancellation"));
        using var cancellation = new CancellationTokenSource();

        var pending = host.CreateFileDirectoryAsync(
            request,
            context,
            cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        release.TrySetResult();

        var completed = await pending;
        var replay = await host.CreateFileDirectoryAsync(
            request,
            context,
            CancellationToken.None);

        Assert.IsType<HostResult<FilePanelResult<FilePanelEntry>>.Success>(completed);
        Assert.IsType<HostResult<FilePanelResult<FilePanelEntry>>.Success>(replay);
        Assert.Equal(1, files[sessionId].CreateDirectoryCount);
    }

    [Fact]
    public async Task ExceptionAfterFileDispatchLeavesAnUncertainReplay()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var files = new FakeFilePanelSessionFactory();
        await using var host = CreateHost(files, clock);
        var sessionId = new SessionId("files-1");
        var root = Root();
        var opened = await OpenAsync(
            host,
            sessionId,
            Owner("panel-1"),
            root);
        files[sessionId].CreateDirectoryOperation = (_, _) =>
            throw new IOException("The provider outcome is unknown.");
        var request = new FilePanelCreateDirectoryHostRequest(
            sessionId,
            new FilePanelCreateDirectoryRequest(
                Child(root, "new-directory"),
                FilePanelMutationPrecondition.MustNotExist));
        var context = Context(
            opened.Descriptor.Revision,
            new IdempotencyKey("exception-after-file-dispatch"));

        var uncertain = await host.CreateFileDirectoryAsync(
            request,
            context,
            CancellationToken.None);
        var replay = await host.CreateFileDirectoryAsync(
            request,
            context,
            CancellationToken.None);

        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            uncertain.Error().Code);
        Assert.Equal(
            HostErrorCode.ResynchronizationRequired,
            replay.Error().Code);
        Assert.Equal(1, files[sessionId].CreateDirectoryCount);
    }

    [Fact]
    public async Task ContextCancellationDeadlineAndRevisionAreAppliedBeforeFileDispatch()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var files = new FakeFilePanelSessionFactory();
        await using var host = CreateHost(files, clock);
        var sessionId = new SessionId("files-1");
        var root = Root();
        var opened = Assert.IsType<HostResult<SessionSnapshot>.Success>(
            await host.EnsureFilePanelSessionAsync(
                new EnsureFilePanelSessionRequest(
                    sessionId,
                    Owner("panel-1"),
                    "Files",
                    root),
                Context(),
                CancellationToken.None));
        var request = new FilePanelListHostRequest(
            sessionId,
            new FilePanelListRequest(root, 20, null, ShowHidden: false));

        var stale = await host.ListFilesAsync(
            request,
            Context(expectedRevision: opened.ResultingRevision - 1),
            CancellationToken.None);
        var expired = await host.ListFilesAsync(
            request,
            Context(deadline: clock.GetUtcNow()),
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await host.ListFilesAsync(
            request,
            Context(),
            cancellation.Token);

        Assert.Equal(HostErrorCode.RevisionConflict, stale.Error().Code);
        Assert.Equal(HostErrorCode.DeadlineExceeded, expired.Error().Code);
        Assert.Equal(HostErrorCode.Cancelled, cancelled.Error().Code);
    }

    [Fact]
    public async Task PanelClosePromptsForItsTransferAndConfirmedCloseLeavesSiblingRunning()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var files = new FakeFilePanelSessionFactory();
        await using var host = CreateHost(files, clock);
        var root = Root();
        var firstId = new SessionId("files-1");
        var secondId = new SessionId("files-2");
        await OpenAsync(host, firstId, Owner("panel-1"), root);
        await OpenAsync(host, secondId, Owner("panel-2"), root);
        _ = (await host.EnqueueFileTransferAsync(
            new FilePanelTransferEnqueueHostRequest(
                firstId,
                Transfer(root, "first-source", "first-destination")),
            Context(),
            CancellationToken.None)).Value();
        _ = (await host.EnqueueFileTransferAsync(
            new FilePanelTransferEnqueueHostRequest(
                secondId,
                Transfer(root, "second-source", "second-destination")),
            Context(),
            CancellationToken.None)).Value();

        var preflight = (await host.CloseAsync(
            CloseScopeRequest.Panel(new PanelInstanceId("panel-1"), CloseDecision.Request),
            Context(),
            CancellationToken.None)).Value();
        var confirmation = Assert.IsType<CloseScopeResult.ConfirmationRequired>(preflight);
        Assert.Equal(firstId, Assert.Single(confirmation.Sessions).SessionId);

        var confirmed = (await host.CloseAsync(
            CloseScopeRequest.Panel(new PanelInstanceId("panel-1"), CloseDecision.Confirm),
            Context(),
            CancellationToken.None)).Value();

        Assert.IsType<CloseScopeResult.Completed>(confirmed);
        Assert.Equal(PanelCloseMode.Force, files[firstId].LastCloseMode);
        Assert.Null(files[secondId].LastCloseMode);
        Assert.True((await files[secondId].SnapshotAsync(CancellationToken.None)).HasActiveWork);
    }

    private static InMemorySessionHostClient CreateHost(
        IFilePanelSessionFactory fileFactory,
        TimeProvider timeProvider,
        ITerminalSessionFactory? terminalFactory = null) => new(
        terminalFactory ?? new FakeTerminalSessionFactory(),
        new DesktopLifecyclePolicy(),
        timeProvider,
        filePanelFactory: fileFactory);

    private static async ValueTask<SessionSnapshot> OpenAsync(
        InMemorySessionHostClient host,
        SessionId sessionId,
        SessionOwner owner,
        FilePanelLocation root) =>
        (await host.EnsureFilePanelSessionAsync(
            new EnsureFilePanelSessionRequest(sessionId, owner, "Files", root),
            Context(),
            CancellationToken.None)).Value();

    private static EnsureFilePanelSessionRequest OpenRequest(string id) => new(
        new SessionId(id),
        Owner($"{id}-panel"),
        "Files",
        Root());

    private static OperationContext Context(
        long? expectedRevision = null,
        IdempotencyKey? idempotencyKey = null,
        DateTimeOffset? deadline = null) => new(
        RequestId.New(),
        new ActorDescriptor(
            new ActorId("user-1"),
            ActorKind.Human,
            "Test user",
            new ClientId("client-1")),
        expectedRevision,
        idempotencyKey,
        CancellationId.New(),
        deadline);

    private static SessionOwner Owner(string panelId) => new(
        HostMode.Desktop,
        new WindowInstanceId("window-1"),
        new WorkspaceInstanceId("workspace-1"),
        new TabInstanceId("tab-1"),
        new PanelInstanceId(panelId));

    private static CapabilitySet AllFileCapabilities() => new(
    [
        SessionCapabilities.FilesList,
        SessionCapabilities.FilesStat,
        SessionCapabilities.FilesPreview,
        SessionCapabilities.FilesCreateDirectory,
        SessionCapabilities.FilesRename,
        SessionCapabilities.FilesDelete,
        SessionCapabilities.FilesTransferEnqueue,
        SessionCapabilities.FilesTransferCancel,
        SessionCapabilities.FilesTransferRetry,
    ]);

    private static FilePanelLocation Root() => new(
        "profile-1",
        "test",
        new FilePanelAddress.Hierarchical(FilePanelPath.Root));

    private static FilePanelLocation Child(FilePanelLocation root, string name) =>
        root.Child(new FilePanelPathSegment(name));

    private static FilePanelResult<FilePanelEntry> SuccessfulDirectory(
        FilePanelCreateDirectoryRequest request) =>
        FilePanelResult<FilePanelEntry>.Success(
            new FilePanelEntry(
                request.Location,
                request.Location.Address is FilePanelAddress.Hierarchical hierarchical
                    ? hierarchical.Path.Name?.Value ?? "directory"
                    : "directory",
                FilePanelEntryKind.Directory,
                null,
                null,
                false));

    private static FilePanelTransferRequest Transfer(
        FilePanelLocation root,
        string source,
        string destination) => new(
        Child(root, source),
        Child(root, destination),
        FilePanelTransferOperation.Copy,
        FilePanelConflictPolicy.Fail);
}
