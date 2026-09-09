using Asura.Application;

namespace Asura.Files.Tests;

public sealed class FileTransferQueueTests
{
    [Fact]
    public async Task SameProviderCopyStreamsAndPublishesCompletion()
    {
        using var root = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(root.Path, "source.txt"), "transfer payload");
        using var client = CreateLocalClient(("local-a", "source", root.Path));
        var profile = Assert.Single(client.Profiles);
        var source = Child(profile.Root, "source.txt");
        var destination = Child(profile.Root, "copied.txt");

        var queued = await client.EnqueueAsync(
            new FilePanelTransferRequest(
                source,
                destination,
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail),
            CancellationToken.None);
        var completed = await WaitForTerminalStateAsync(client, queued.Value!.Id);

        Assert.True(
            completed.State == FilePanelTransferState.Completed,
            $"{completed.Error?.StableCode}: {completed.Error?.Message}");
        Assert.Equal("transfer payload".Length, completed.BytesTransferred);
        Assert.Equal("transfer payload", await File.ReadAllTextAsync(Path.Combine(root.Path, "copied.txt")));
    }

    [Fact]
    public async Task ConflictPoliciesSkipOrChooseStructuredKeepBothDestination()
    {
        using var root = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(root.Path, "source.txt"), "new");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "target.txt"), "existing");
        using var client = CreateLocalClient(("local-a", "source", root.Path));
        var profile = Assert.Single(client.Profiles);
        var source = Child(profile.Root, "source.txt");
        var destination = Child(profile.Root, "target.txt");

        var skipped = await client.EnqueueAsync(
            new FilePanelTransferRequest(
                source,
                destination,
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Skip),
            CancellationToken.None);
        var kept = await client.EnqueueAsync(
            new FilePanelTransferRequest(
                source,
                destination,
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.KeepBoth),
            CancellationToken.None);
        var keptCompleted = await WaitForTerminalStateAsync(client, kept.Value!.Id);

        Assert.Equal(FilePanelTransferState.Skipped, skipped.Value!.State);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(root.Path, "target.txt")));
        Assert.Equal(FilePanelTransferState.Completed, keptCompleted.State);
        var address = Assert.IsType<FilePanelAddress.Hierarchical>(keptCompleted.EffectiveDestination.Address);
        Assert.Equal("target (copy 2).txt", address.Path.Name?.Value);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(root.Path, "target (copy 2).txt")));
    }

    [Fact]
    public async Task CrossProviderMoveUsesBackpressuredStreamThenDeletesVersionedSource()
    {
        using var sourceRoot = TemporaryDirectory.Create();
        using var destinationRoot = TemporaryDirectory.Create();
        var payload = Enumerable.Range(0, 200_000).Select(index => (byte)(index % 251)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(sourceRoot.Path, "source.bin"), payload);
        using var client = CreateLocalClient(
            ("local-source", "source", sourceRoot.Path),
            ("local-destination", "destination", destinationRoot.Path));
        var sourceProfile = client.Profiles.Single(profile => string.Equals(profile.Id, "local-source", StringComparison.Ordinal));
        var destinationProfile = client.Profiles.Single(profile => string.Equals(profile.Id, "local-destination", StringComparison.Ordinal));

        var queued = await client.EnqueueAsync(
            new FilePanelTransferRequest(
                Child(sourceProfile.Root, "source.bin"),
                Child(destinationProfile.Root, "moved.bin"),
                FilePanelTransferOperation.Move,
                FilePanelConflictPolicy.Fail),
            CancellationToken.None);
        var completed = await WaitForTerminalStateAsync(client, queued.Value!.Id);

        Assert.True(
            completed.State == FilePanelTransferState.Completed,
            $"{completed.Error?.StableCode}: {completed.Error?.Message}");
        Assert.False(File.Exists(Path.Combine(sourceRoot.Path, "source.bin")));
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(destinationRoot.Path, "moved.bin")));
    }

    [Fact]
    public async Task CrossProviderReadUsesChunksBoundedByTheSourceReadLimit()
    {
        var sourceProfileId = new FileProviderProfileId("bounded-source");
        var destinationProfileId = new FileProviderProfileId("bounded-destination");
        var sourceAuthority = new FileAuthority("source");
        var destinationAuthority = new FileAuthority("destination");
        var sourceLimits = new FileProviderLimits(
            maximumListPageSize: 100,
            maximumReadBytes: 512,
            maximumBufferSize: 256);
        var destinationLimits = new FileProviderLimits(
            maximumListPageSize: 100,
            maximumReadBytes: 4_096,
            maximumBufferSize: 256);
        var sourceProvider = new InMemoryFileProvider(
            sourceProfileId,
            sourceAuthority,
            sourceLimits);
        var destinationProvider = new InMemoryFileProvider(
            destinationProfileId,
            destinationAuthority,
            destinationLimits);
        var sourceRoot = new FileLocation(
            sourceProfileId,
            sourceAuthority,
            FilePath.Root);
        var destinationRoot = new FileLocation(
            destinationProfileId,
            destinationAuthority,
            FilePath.Root);
        var source = sourceRoot.Child(new FilePathSegment("large.bin"));
        var destination = destinationRoot.Child(new FilePathSegment("large.bin"));
        var payload = Enumerable.Range(0, 2_048)
            .Select(index => (byte)(index % 251))
            .ToArray();
        await using (var content = new MemoryStream(payload))
        {
            var seeded = await sourceProvider.WriteAsync(
                new FileWriteRequest(
                    source,
                    payload.Length,
                    bufferSize: 256,
                    new FileMutationPrecondition.MustNotExist()),
                content,
                progress: null,
                CancellationToken.None);
            Assert.True(seeded.IsSuccess, seeded.Error?.Message);
        }

        using var client = new FilePanelClient(
        [
            new FileProviderRegistration(
                "Source",
                FileProviderFamily.Posix,
                sourceProvider,
                sourceRoot),
            new FileProviderRegistration(
                "Destination",
                FileProviderFamily.Posix,
                destinationProvider,
                destinationRoot),
        ]);
        var sourcePanelRoot = client.Profiles.Single(profile => string.Equals(profile.Id, sourceProfileId.Value, StringComparison.Ordinal)).Root;
        var destinationPanelRoot = client.Profiles.Single(profile => string.Equals(profile.Id, destinationProfileId.Value, StringComparison.Ordinal)).Root;
        var queued = await client.EnqueueAsync(
            new FilePanelTransferRequest(
                Child(sourcePanelRoot, "large.bin"),
                Child(destinationPanelRoot, "large.bin"),
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail),
            CancellationToken.None);
        var completed = await WaitForTerminalStateAsync(client, queued.Value!.Id);

        Assert.True(
            completed.State == FilePanelTransferState.Completed,
            $"{completed.Error?.StableCode}: {completed.Error?.Message}");
        await using var copied = new MemoryStream();
        var read = await destinationProvider.ReadAsync(
            new FileReadRequest(
                destination,
                offset: 0,
                maximumBytes: payload.Length,
                bufferSize: 256),
            copied,
            progress: null,
            CancellationToken.None);
        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(payload, copied.ToArray());
    }

    [Fact]
    public async Task CrossProviderDirectoryCopyStreamsFilesAndPreservesEmptyFolders()
    {
        using var sourceRoot = TemporaryDirectory.Create();
        using var destinationRoot = TemporaryDirectory.Create();
        var sourceDirectory = Directory.CreateDirectory(
            Path.Combine(sourceRoot.Path, "project"));
        var nestedDirectory = sourceDirectory.CreateSubdirectory("src");
        _ = sourceDirectory.CreateSubdirectory("empty");
        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory.FullName, "README.md"),
            "project");
        await File.WriteAllBytesAsync(
            Path.Combine(nestedDirectory.FullName, "app.bin"),
            [1, 2, 3, 4, 5]);
        using var client = CreateLocalClient(
            ("local-source", "source", sourceRoot.Path),
            ("local-destination", "destination", destinationRoot.Path));
        var sourceProfile = client.Profiles.Single(profile => string.Equals(profile.Id, "local-source", StringComparison.Ordinal));
        var destinationProfile = client.Profiles.Single(profile => string.Equals(profile.Id, "local-destination", StringComparison.Ordinal));

        var queued = await client.EnqueueAsync(
            new FilePanelTransferRequest(
                Child(sourceProfile.Root, "project"),
                Child(destinationProfile.Root, "project-copy"),
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail),
            CancellationToken.None);
        var completed = await WaitForTerminalStateAsync(client, queued.Value!.Id);

        Assert.True(
            completed.State == FilePanelTransferState.Completed,
            $"{completed.Error?.StableCode}: {completed.Error?.Message}");
        Assert.Equal(12, completed.BytesTransferred);
        Assert.Equal(
            "project",
            await File.ReadAllTextAsync(
                Path.Combine(destinationRoot.Path, "project-copy", "README.md")));
        Assert.Equal(
            [1, 2, 3, 4, 5],
            await File.ReadAllBytesAsync(
                Path.Combine(destinationRoot.Path, "project-copy", "src", "app.bin")));
        Assert.True(Directory.Exists(
            Path.Combine(destinationRoot.Path, "project-copy", "empty")));
    }

    [Fact]
    public async Task RunningTransferCanBeCancelledAndRetriedWithANewIdentity()
    {
        var provider = new BlockingTransferProvider();
        using var client = new FilePanelClient(
        [
            new FileProviderRegistration(
                "Blocking",
                FileProviderFamily.Posix,
                provider,
                provider.Root),
        ]);
        var profile = Assert.Single(client.Profiles);
        var request = new FilePanelTransferRequest(
            Child(profile.Root, "source"),
            Child(profile.Root, "destination"),
            FilePanelTransferOperation.Copy,
            FilePanelConflictPolicy.Fail);
        var queued = await client.EnqueueAsync(request, CancellationToken.None);
        await WaitForStateAsync(client, queued.Value!.Id, FilePanelTransferState.Running);

        var cancelled = await client.CancelAsync(queued.Value.Id, CancellationToken.None);
        var cancellationRequested = client.Transfers.Single(
            transfer => transfer.Id == queued.Value.Id);
        var cancelledSnapshot = await WaitForTerminalStateAsync(client, queued.Value.Id);
        var retried = await client.RetryAsync(queued.Value.Id, CancellationToken.None);

        Assert.True(cancelled.IsSuccess, cancelled.Error?.Message);
        Assert.True(cancellationRequested.CancellationRequested);
        Assert.False(cancellationRequested.CanCancel);
        Assert.Equal(FilePanelTransferState.Cancelled, cancelledSnapshot.State);
        Assert.True(retried.IsSuccess, retried.Error?.Message);
        Assert.NotEqual(queued.Value.Id, retried.Value!.Id);
        _ = await client.CancelAsync(retried.Value.Id, CancellationToken.None);
        _ = await WaitForTerminalStateAsync(client, retried.Value.Id);
    }

    [Fact]
    public async Task TransfersRemainQueuedUntilThePreviousTransferFinishes()
    {
        var provider = new BlockingTransferProvider();
        using var client = new FilePanelClient(
        [
            new FileProviderRegistration(
                "Blocking",
                FileProviderFamily.Posix,
                provider,
                provider.Root),
        ]);
        var profile = Assert.Single(client.Profiles);
        var first = await client.EnqueueAsync(
            new FilePanelTransferRequest(
                Child(profile.Root, "source-1"),
                Child(profile.Root, "destination-1"),
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail),
            CancellationToken.None);
        await WaitForStateAsync(
            client,
            first.Value!.Id,
            FilePanelTransferState.Running);
        var second = await client.EnqueueAsync(
            new FilePanelTransferRequest(
                Child(profile.Root, "source-2"),
                Child(profile.Root, "destination-2"),
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail),
            CancellationToken.None);
        var secondId = second.Value!.Id;

        Assert.Equal(
            FilePanelTransferState.Queued,
            client.Transfers.Single(item => item.Id == secondId).State);

        _ = await client.CancelAsync(first.Value.Id, CancellationToken.None);
        _ = await WaitForTerminalStateAsync(client, first.Value.Id);
        await WaitForStateAsync(
            client,
            secondId,
            FilePanelTransferState.Running);
        _ = await client.CancelAsync(secondId, CancellationToken.None);
        _ = await WaitForTerminalStateAsync(client, secondId);
    }

    private static FilePanelClient CreateLocalClient(
        params (string ProfileId, string Authority, string RootPath)[] profiles)
    {
        var registrations = profiles.Select(profile =>
        {
            var options = new LocalFileProviderOptions(
                new FileProviderProfileId(profile.ProfileId),
                new FileAuthority(profile.Authority),
                profile.RootPath);
            var provider = LocalFileProvider.CreateForCurrentPlatform(options);
            return new FileProviderRegistration(
                profile.ProfileId,
                OperatingSystem.IsWindows() ? FileProviderFamily.Windows : FileProviderFamily.Posix,
                provider,
                new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root));
        });
        return new FilePanelClient(registrations);
    }

    private static FilePanelLocation Child(FilePanelLocation root, string name) =>
        root.Child(new FilePanelPathSegment(name));

    private static async Task<FilePanelTransferSnapshot> WaitForTerminalStateAsync(
        IFileTransferQueueClient queue,
        FilePanelTransferId id)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var snapshot = queue.Transfers.Single(item => item.Id == id);
            if (snapshot.State is FilePanelTransferState.Completed
                or FilePanelTransferState.Failed
                or FilePanelTransferState.Cancelled
                or FilePanelTransferState.Skipped)
            {
                return snapshot;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The test transfer did not reach a terminal state.");
    }

    private static async Task WaitForStateAsync(
        IFileTransferQueueClient queue,
        FilePanelTransferId id,
        FilePanelTransferState state)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (queue.Transfers.Single(item => item.Id == id).State == state)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"The test transfer did not reach {state}.");
    }

    private sealed class BlockingTransferProvider : IFileProvider
    {
        public BlockingTransferProvider()
        {
            ProfileId = new FileProviderProfileId("blocking");
            Root = new FileLocation(ProfileId, new FileAuthority("test"), FilePath.Root);
            Capabilities = new FileProviderCapabilities(
                FileProviderCapability.Copy,
                FileNameComparison.CaseSensitive,
                new FileProviderLimits(100, 1024, 1024));
        }

        public FileProviderProfileId ProfileId { get; }

        public FileLocation Root { get; }

        public FileProviderCapabilities Capabilities { get; }

        public ValueTask<FileProviderResult<FilePage>> ListAsync(
            FileListRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileEntry>> StatAsync(
            FileStatRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
            FileReadRequest request,
            Stream destination,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(
            FileWriteRequest request,
            Stream source,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(
            FileCreateDirectoryRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<FileProviderResult<FileEntry>> RenameAsync(
            FileRenameRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public async ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(
            FileTransferRequest request,
            IProgress<FileTransferProgress>? progress,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return FileProviderResult<FileTransferReceipt>.Failure(FileProviderError.Create(
                    FileProviderErrorCode.IoFailure,
                    "The blocking transfer ended unexpectedly."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return FileProviderResult<FileTransferReceipt>.Failure(FileProviderError.Create(
                    FileProviderErrorCode.Cancelled,
                    "The blocking transfer was cancelled."));
            }
        }

        public ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(
            FileDeleteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
