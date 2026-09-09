using System.Runtime.CompilerServices;
using Asura.Application;
using Asura.Core;

namespace Asura.Files.Tests;

public sealed class FilePanelSessionTests
{
    [Fact]
    public async Task ForceCloseCancelsOnlyTransfersOwnedByThatSession()
    {
        var queue = new RecordingTransferQueue();
        var root = Root();
        var factory = new FilePanelSessionFactory(new UnusedFilePanelClient(root), queue);
        await using var first = await factory.CreateAsync(
            new SessionId("files-1"),
            root,
            CancellationToken.None);
        await using var second = await factory.CreateAsync(
            new SessionId("files-2"),
            root,
            CancellationToken.None);

        var firstTransfer = await first.EnqueueTransferAsync(
            Transfer(root, "first-source", "first-destination"),
            CancellationToken.None);
        var secondTransfer = await second.EnqueueTransferAsync(
            Transfer(root, "second-source", "second-destination"),
            CancellationToken.None);

        Assert.True((await first.SnapshotAsync(CancellationToken.None)).HasActiveWork);
        Assert.True((await second.SnapshotAsync(CancellationToken.None)).HasActiveWork);
        Assert.Equal(
            PanelCloseOutcome.ConfirmationRequired,
            await first.CloseAsync(PanelCloseMode.Graceful, CancellationToken.None));

        var foreignCancellation = await first.CancelTransferAsync(
            secondTransfer.Value!.Id,
            CancellationToken.None);
        Assert.False(foreignCancellation.IsSuccess);
        Assert.Equal("file_transfer_not_owned_by_panel", foreignCancellation.Error!.StableCode);

        Assert.Equal(
            PanelCloseOutcome.ForceTerminated,
            await first.CloseAsync(PanelCloseMode.Force, CancellationToken.None));

        Assert.Contains(firstTransfer.Value!.Id, queue.CancelledIds);
        Assert.DoesNotContain(secondTransfer.Value.Id, queue.CancelledIds);
        Assert.False((await first.SnapshotAsync(CancellationToken.None)).HasActiveWork);
        Assert.True((await second.SnapshotAsync(CancellationToken.None)).HasActiveWork);
    }

    [Fact]
    public async Task RetryCreatesANewTransferOwnedByTheSameSession()
    {
        var queue = new RecordingTransferQueue();
        var root = Root();
        var factory = new FilePanelSessionFactory(new UnusedFilePanelClient(root), queue);
        await using var session = await factory.CreateAsync(
            new SessionId("files-1"),
            root,
            CancellationToken.None);
        var original = await session.EnqueueTransferAsync(
            Transfer(root, "source", "destination"),
            CancellationToken.None);
        queue.Fail(original.Value!.Id);

        var retried = await session.RetryTransferAsync(
            original.Value.Id,
            CancellationToken.None);
        var cancelled = await session.CancelTransferAsync(
            retried.Value!.Id,
            CancellationToken.None);

        Assert.True(retried.IsSuccess, retried.Error?.Message);
        Assert.NotEqual(original.Value.Id, retried.Value.Id);
        Assert.True(cancelled.IsSuccess, cancelled.Error?.Message);
        Assert.Contains(retried.Value.Id, queue.CancelledIds);
    }

    [Fact]
    public void FactoryAdvertisesEveryFileOperationWithoutProviderTypes()
    {
        var root = Root();
        var factory = new FilePanelSessionFactory(
            new UnusedFilePanelClient(root),
            new RecordingTransferQueue());

        Assert.True(factory.Capabilities.Contains(SessionCapabilities.FilesList));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.FilesSearch));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.FilesStat));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.FilesPreview));
        Assert.True(factory.Capabilities.Contains(
            SessionCapabilities.FilesReadAccessControl));
        Assert.True(factory.Capabilities.Contains(
            SessionCapabilities.FilesTransfersRead));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.FilesCreateDirectory));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.FilesRename));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.FilesDelete));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.FilesTransferEnqueue));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.FilesTransferCancel));
        Assert.True(factory.Capabilities.Contains(SessionCapabilities.FilesTransferRetry));
    }

    [Fact]
    public async Task SessionForwardsProviderSearchWithoutMaterializingUIState()
    {
        var root = Root();
        var client = new UnusedFilePanelClient(root);
        var factory = new FilePanelSessionFactory(
            client,
            new RecordingTransferQueue());
        await using var session = await factory.CreateAsync(
            new SessionId("files-search"),
            root,
            CancellationToken.None);
        var request = new FilePanelSearchRequest(
            root,
            "match",
            FilePanelDiscoveryScope.Subtree,
            showHidden: false);

        var results = new List<FilePanelResult<FilePanelEntry>>();
        await foreach (var result in session.SearchAsync(
            request,
            CancellationToken.None))
        {
            results.Add(result);
        }

        Assert.Same(request, Assert.Single(client.SearchRequests));
        Assert.Equal("match.txt", Assert.Single(results).Value?.Name);
    }

    [Fact]
    public async Task FactoryCapturesTrustedMetadataFromTheExactProfileAndInitialPrefix()
    {
        var profileRoot = Root().Child(new FilePanelPathSegment("configured"));
        var initialPrefix = profileRoot.Child(new FilePanelPathSegment("session-prefix"));
        var factory = new FilePanelSessionFactory(
            new UnusedFilePanelClient(profileRoot),
            new RecordingTransferQueue());

        await using var session = await factory.CreateAsync(
            new SessionId("files-metadata"),
            initialPrefix,
            CancellationToken.None);

        Assert.Equal(initialPrefix, session.Metadata.TrustedRoot);
        Assert.Equal(FilePanelCapability.List, session.Metadata.Capabilities);
        Assert.Equal(100, session.Metadata.MaximumListPageSize);
        Assert.Equal(1024, session.Metadata.MaximumPreviewBytes);
    }

    [Fact]
    public async Task FactoryRejectsAHierarchicalInitialPrefixOutsideTheProfileRoot()
    {
        var profileRoot = Root().Child(new FilePanelPathSegment("configured"));
        var outside = Root().Child(new FilePanelPathSegment("outside"));
        var factory = new FilePanelSessionFactory(
            new UnusedFilePanelClient(profileRoot),
            new RecordingTransferQueue());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await factory.CreateAsync(
                new SessionId("files-outside"),
                outside,
                CancellationToken.None));
    }

    [Fact]
    public async Task FactoryPreservesStructuredObjectSessionsForHumanFileViewerUse()
    {
        var objectLocation = new FilePanelLocation(
            "profile-objects",
            "bucket",
            new FilePanelAddress.ObjectKey("literal/../object"));
        var containerLocation = new FilePanelLocation(
            "profile-objects",
            "bucket",
            new FilePanelAddress.ContainerRoot());
        var factory = new FilePanelSessionFactory(
            new UnusedFilePanelClient(objectLocation, FileProviderFamily.S3),
            new RecordingTransferQueue());

        await using var session = await factory.CreateAsync(
            new SessionId("files-object"),
            objectLocation,
            CancellationToken.None);

        Assert.Equal(objectLocation, session.Metadata.TrustedRoot);
        Assert.IsType<FilePanelAddress.ObjectKey>(
            session.Metadata.TrustedRoot.Address);

        var containerFactory = new FilePanelSessionFactory(
            new UnusedFilePanelClient(containerLocation, FileProviderFamily.S3),
            new RecordingTransferQueue());
        await using var containerSession = await containerFactory.CreateAsync(
            new SessionId("files-container"),
            containerLocation,
            CancellationToken.None);
        Assert.IsType<FilePanelAddress.ContainerRoot>(
            containerSession.Metadata.TrustedRoot.Address);
    }

    [Fact]
    public async Task FactoryPreservesRecoveredS3LocationsAcrossSupportedAddressShapes()
    {
        var root = new FilePanelLocation(
            "profile-s3",
            "bucket",
            new FilePanelAddress.ContainerRoot());
        var objectChild = new FilePanelLocation(
            root.ProviderProfileId,
            root.Authority,
            new FilePanelAddress.ObjectKey("folder/literal//object"));
        var hierarchicalChild = new FilePanelLocation(
            root.ProviderProfileId,
            root.Authority,
            new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments(
                [
                    new FilePanelPathSegment("folder"),
                    new FilePanelPathSegment("child"),
                ])));
        var factory = new FilePanelSessionFactory(
            new UnusedFilePanelClient(root, FileProviderFamily.S3),
            new RecordingTransferQueue());

        await using var objectSession = await factory.CreateAsync(
            new SessionId("files-s3-object-recovery"),
            objectChild,
            CancellationToken.None);
        await using var hierarchicalSession = await factory.CreateAsync(
            new SessionId("files-s3-prefix-recovery"),
            hierarchicalChild,
            CancellationToken.None);

        Assert.Equal(objectChild, objectSession.Metadata.TrustedRoot);
        Assert.Equal(hierarchicalChild, hierarchicalSession.Metadata.TrustedRoot);
    }

    [Fact]
    public async Task FactoryRejectsMismatchedStructuredAddressFamilies()
    {
        var hierarchicalRoot = Root();
        var objectRoot = new FilePanelLocation(
            hierarchicalRoot.ProviderProfileId,
            hierarchicalRoot.Authority,
            new FilePanelAddress.ObjectKey("object"));
        var containerRoot = new FilePanelLocation(
            hierarchicalRoot.ProviderProfileId,
            hierarchicalRoot.Authority,
            new FilePanelAddress.ContainerRoot());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new FilePanelSessionFactory(
                    new UnusedFilePanelClient(hierarchicalRoot),
                    new RecordingTransferQueue())
                .CreateAsync(
                    new SessionId("files-object-against-hierarchy"),
                    objectRoot,
                    CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new FilePanelSessionFactory(
                    new UnusedFilePanelClient(containerRoot),
                    new RecordingTransferQueue())
                .CreateAsync(
                    new SessionId("files-hierarchy-against-container"),
                    hierarchicalRoot,
                    CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await new FilePanelSessionFactory(
                    new UnusedFilePanelClient(objectRoot),
                    new RecordingTransferQueue())
                .CreateAsync(
                    new SessionId("files-container-against-object"),
                    containerRoot,
                    CancellationToken.None));
    }

    private static FilePanelTransferRequest Transfer(
        FilePanelLocation root,
        string source,
        string destination) => new(
        root.Child(new FilePanelPathSegment(source)),
        root.Child(new FilePanelPathSegment(destination)),
        FilePanelTransferOperation.Copy,
        FilePanelConflictPolicy.Fail);

    private static FilePanelLocation Root() => new(
        "profile-1",
        "test",
        new FilePanelAddress.Hierarchical(FilePanelPath.Root));

    private sealed class UnusedFilePanelClient(
        FilePanelLocation root,
        FileProviderFamily family = FileProviderFamily.Posix)
        : IFilePanelClient
    {
        public List<FilePanelSearchRequest> SearchRequests { get; } = [];

        public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; } =
        [
            new FileProviderProfileDescriptor(
                root.ProviderProfileId,
                "Test",
                family,
                root,
                FilePanelCapability.List,
                100,
                1024),
        ];

        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
            FilePanelListRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async IAsyncEnumerable<FilePanelResult<FilePanelEntry>> SearchAsync(
            FilePanelSearchRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SearchRequests.Add(request);
            yield return FilePanelResult<FilePanelEntry>.Success(
                new FilePanelEntry(
                    request.Location.Child(
                        new FilePanelPathSegment("match.txt")),
                    "match.txt",
                    FilePanelEntryKind.File,
                    1,
                    null,
                    false));
            await Task.CompletedTask;
        }

        public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
            FilePanelPreviewRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
            FilePanelCreateDirectoryRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
            FilePanelRenameRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
            FilePanelDeleteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingTransferQueue : IFileTransferQueueClient
    {
        private readonly List<FilePanelTransferSnapshot> _transfers = [];

        public event EventHandler? TransfersChanged;

        public IReadOnlyList<FilePanelTransferSnapshot> Transfers => [.. _transfers];

        public HashSet<FilePanelTransferId> CancelledIds { get; } = [];

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
            FilePanelTransferRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new FilePanelTransferSnapshot(
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
            _transfers.Add(snapshot);
            TransfersChanged?.Invoke(this, EventArgs.Empty);
            return ValueTask.FromResult(FilePanelResult<FilePanelTransferSnapshot>.Success(snapshot));
        }

        public ValueTask<FilePanelResult<Unit>> CancelAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = _transfers.FindIndex(item => item.Id == id);
            if (index < 0)
            {
                return ValueTask.FromResult(FilePanelResult<Unit>.Failure(new FilePanelError(
                    FilePanelErrorCode.NotFound,
                    "missing",
                    "Missing transfer.",
                    false)));
            }

            CancelledIds.Add(id);
            _transfers[index] = _transfers[index] with
            {
                State = FilePanelTransferState.Cancelled,
                Stage = "Cancelled",
                CompletedAt = DateTimeOffset.UtcNow,
            };
            TransfersChanged?.Invoke(this, EventArgs.Empty);
            return ValueTask.FromResult(FilePanelResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken)
        {
            var original = _transfers.Single(item => item.Id == id);
            return EnqueueAsync(original.Request, cancellationToken);
        }

        public void Fail(FilePanelTransferId id)
        {
            var index = _transfers.FindIndex(item => item.Id == id);
            _transfers[index] = _transfers[index] with
            {
                State = FilePanelTransferState.Failed,
                Stage = "Failed",
                Error = new FilePanelError(
                    FilePanelErrorCode.IoFailure,
                    "failed",
                    "Failed.",
                    true),
                CompletedAt = DateTimeOffset.UtcNow,
            };
            TransfersChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
