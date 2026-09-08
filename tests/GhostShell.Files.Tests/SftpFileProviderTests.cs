using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Files.Tests;

public sealed class SftpFileProviderTests
{
    [Fact]
    public async Task A_listed_remote_file_replaced_with_a_link_is_rechecked_for_read_and_permissions()
    {
        var sessions = new FakeRemoteSessionFactory();
        var provider = new SftpFileProvider(sessions, RemoteProviderTestProfiles.SftpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);
        var location = root.Child(new FilePathSegment("listed"));
        await using var source = new MemoryStream("ordinary"u8.ToArray());
        var created = await provider.WriteAsync(new FileWriteRequest(location, source.Length, 1024, new FileMutationPrecondition.MustNotExist()), source, null, CancellationToken.None);
        Assert.True(created.IsSuccess, created.Error?.Message);
        Assert.True((await provider.ListAsync(new FileListRequest(root, 10), CancellationToken.None)).IsSuccess);
        sessions.ReplaceFileWithLink("/listed");
        await using var destination = new MemoryStream();
        var read = await provider.ReadAsync(new FileReadRequest(location, 0, 100, 100), destination, null, CancellationToken.None);
        var acl = await provider.GetAccessControlAsync(new FileAccessControlRequest(location), CancellationToken.None);
        var chmod = await provider.SetAccessControlAsync(new FileSetAccessControlRequest(location, new FilePanelPosixMode(0b111_111_111)), CancellationToken.None);
        Assert.False(read.IsSuccess);
        Assert.False(acl.IsSuccess);
        Assert.False(chmod.IsSuccess);
        Assert.Empty(destination.ToArray());
        Assert.True(sessions.StatPaths.Count(path => path == "/listed") >= 3);
    }

    [Fact]
    public void ProviderReusesConnectionIdentityAndDoesNotClaimUnsafeCapabilities()
    {
        var options = RemoteProviderTestProfiles.SftpOptions();
        var provider = new SftpFileProvider(new FakeRemoteSessionFactory(), options);

        Assert.Equal(options.Connection.Id.Value, provider.Authority.Value);
        Assert.Same(options.Connection, provider.Connection);
        Assert.Equal(FileNameComparison.CaseSensitive, provider.Capabilities.NameComparison);
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.ResumableTransfer));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.AtomicReplace));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.ServerSideCopy));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.Symlinks));
        // SFTP does carry the mode: it is in the same attribute block as the
        // size, so a listing already has it and a chmod is one message.
        Assert.True(provider.Capabilities.Supports(FileProviderCapability.Permissions));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.Versioning));
        Assert.False(provider.Capabilities.Supports(FileProviderCapability.Checksum));
        Assert.True(provider.Capabilities.Supports(FileProviderCapability.StreamingWrite));
    }

    [Fact]
    public void PosixSpecialFilesMapToOtherInsteadOfRegularFiles()
    {
        Assert.Equal(
            FileEntryKind.Other,
            SshNetSftpSessionFactory.ClassifyEntryKind(
                isSymbolicLink: false,
                isDirectory: false,
                isRegularFile: false));
    }

    [Fact]
    public void DirectMetadataLookupTreatsCanonicalPathChangesAsLinks()
    {
        Assert.Equal(
            FileEntryKind.Link,
            SshNetSftpSessionFactory.ClassifyEntryKind(
                isSymbolicLink: false,
                isDirectory: true,
                isRegularFile: false,
                canonicalPathChanged: true));
        Assert.True(
            SshNetSftpSessionFactory.CanonicalPathChanged(
                "/srv/data/link",
                "/mnt/target"));
        Assert.False(
            SshNetSftpSessionFactory.CanonicalPathChanged(
                "/srv/data/",
                "/srv/data"));
    }

    [Fact]
    public async Task WholePathLinkInspectionAvoidsWalkingEveryAncestor()
    {
        var sessions = new FakeRemoteSessionFactory
        {
            StatDetectsAnyLinkInPath = true,
        };
        sessions.SeedDirectory("/home");
        sessions.SeedDirectory("/home/coder");
        var provider = new SftpFileProvider(
            sessions,
            RemoteProviderTestProfiles.SftpOptions());
        var location = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root)
            .Child(new FilePathSegment("home"))
            .Child(new FilePathSegment("coder"));

        var result = await provider.ListAsync(
            new FileListRequest(location, 10),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(["/home/coder"], sessions.StatPaths);
    }

    [Fact]
    public async Task ContinuationPagesReuseOneRemoteDirectorySnapshot()
    {
        var sessions = new FakeRemoteSessionFactory
        {
            ListingOverride = _ =>
            [
                new RemoteFileEntry(
                    "alpha.txt",
                    FileEntryKind.File,
                    1,
                    DateTimeOffset.UnixEpoch,
                    "alpha-revision"),
                new RemoteFileEntry(
                    "beta.txt",
                    FileEntryKind.File,
                    1,
                    DateTimeOffset.UnixEpoch,
                    "beta-revision"),
                new RemoteFileEntry(
                    "gamma.txt",
                    FileEntryKind.File,
                    1,
                    DateTimeOffset.UnixEpoch,
                    "gamma-revision"),
            ],
        };
        var provider = new SftpFileProvider(
            sessions,
            RemoteProviderTestProfiles.SftpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        var first = await provider.ListAsync(
            new FileListRequest(root, pageSize: 2),
            CancellationToken.None);
        var second = await provider.ListAsync(
            new FileListRequest(root, pageSize: 2, first.Value!.ContinuationToken),
            CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Equal(2, first.Value.Items.Length);
        Assert.Single(second.Value!.Items);
        Assert.Equal(1, sessions.OpenCount);
    }

    [Fact]
    public async Task MetadataReconnectRetriesExactlyOnceOnAFreshSession()
    {
        var sessions = new FakeRemoteSessionFactory { FailOpenCount = 1 };
        var options = RemoteProviderTestProfiles.SftpOptions(
            reconnectPolicy: RemoteMetadataReconnectPolicy.RetryOnce);
        var provider = new SftpFileProvider(sessions, options);
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        var result = await provider.StatAsync(new FileStatRequest(root), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, sessions.OpenCount);
    }

    [Fact]
    public void InsecureHostKeyPolicyProducesVisibleDiagnostic()
    {
        var provider = new SftpFileProvider(
            new FakeRemoteSessionFactory(),
            RemoteProviderTestProfiles.SftpOptions(SshHostKeyPolicy.InsecureIgnore));

        var diagnostic = Assert.Single(provider.Diagnostics);
        Assert.Equal("sftp_host_key_verification_disabled", diagnostic.StableCode);
    }

    [Fact]
    public void AcceptNewStoreNeverOverwritesAConcurrentPublicKey()
    {
        var store = new InMemorySftpKnownHostStore();
        var connectionId = new ConnectionId("server");
        var first = Candidate(1);
        var changed = Candidate(2);

        Assert.Equal(
            SshHostKeyVerification.Trusted,
            store.Verify(connectionId, SshHostKeyPolicy.AcceptNew, first));
        Assert.Equal(
            SshHostKeyVerification.Changed,
            store.Verify(connectionId, SshHostKeyPolicy.AcceptNew, changed));
        Assert.Equal(
            SshHostKeyVerification.Trusted,
            store.Verify(connectionId, SshHostKeyPolicy.Strict, first));
    }

    [Fact]
    public void StrictAndAcceptNewPoliciesDistinguishUnknownAndChangedKeys()
    {
        var presented = Candidate(1);
        var strict = RemoteProviderTestProfiles.SftpOptions(SshHostKeyPolicy.Strict).Connection;
        var strictDecision = SftpHostKeyPolicyEvaluator.Evaluate(
            strict,
            new InMemorySftpKnownHostStore(),
            presented);
        Assert.False(strictDecision.Trusted);
        Assert.Equal(RemoteFileSessionErrorCode.HostKeyUnknown, strictDecision.Failure);

        var acceptNew = RemoteProviderTestProfiles.SftpOptions(SshHostKeyPolicy.AcceptNew).Connection;
        var store = new InMemorySftpKnownHostStore();
        Assert.True(SftpHostKeyPolicyEvaluator.Evaluate(acceptNew, store, presented).Trusted);
        var changed = SftpHostKeyPolicyEvaluator.Evaluate(
            acceptNew,
            store,
            Candidate(2));
        Assert.False(changed.Trusted);
        Assert.Equal(RemoteFileSessionErrorCode.HostKeyChanged, changed.Failure);
    }

    [Theory]
    [InlineData((int)RemoteFileSessionErrorCode.HostKeyUnknown, FileProviderErrorCode.HostKeyUnknown)]
    [InlineData((int)RemoteFileSessionErrorCode.HostKeyChanged, FileProviderErrorCode.HostKeyChanged)]
    [InlineData((int)RemoteFileSessionErrorCode.HostKeyStoreInvalid, FileProviderErrorCode.HostKeyStoreInvalid)]
    public async Task HostKeyFailuresRemainTypedAtProviderBoundary(
        int sessionErrorValue,
        FileProviderErrorCode providerError)
    {
        var sessions = new FakeRemoteSessionFactory
        {
            OpenError = (RemoteFileSessionErrorCode)sessionErrorValue,
        };
        var provider = new SftpFileProvider(sessions, RemoteProviderTestProfiles.SftpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        var result = await provider.ListAsync(
            new FileListRequest(root, 10),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(providerError, result.Error!.Code);
    }

    [Fact]
    public async Task AuthenticationFailureIsNotReportedAsFilePermissionDenial()
    {
        var sessions = new FakeRemoteSessionFactory
        {
            OpenError = RemoteFileSessionErrorCode.AuthenticationFailed,
        };
        var provider = new SftpFileProvider(
            sessions,
            RemoteProviderTestProfiles.SftpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        var result = await provider.ListAsync(
            new FileListRequest(root, 10),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.AuthenticationRequired, result.Error!.Code);
        Assert.Equal("authentication_required", result.Error.StableCode);
    }

    [Fact]
    public async Task HostileListingOverEntryBudgetIsRejectedBeforeSorting()
    {
        var sessions = new FakeRemoteSessionFactory
        {
            ListingOverride = _ => new RepeatedRemoteEntryList(
                RemoteDirectorySnapshot.MaximumEntryCount + 1,
                ListedFile("entry")),
        };
        var provider = new SftpFileProvider(sessions, RemoteProviderTestProfiles.SftpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        var result = await provider.ListAsync(
            new FileListRequest(root, 1),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.LimitExceeded, result.Error!.Code);
    }

    [Fact]
    public async Task HostileListingOverAggregateNameBudgetIsRejectedBeforeSorting()
    {
        const int NameLength = 1024;
        var sessions = new FakeRemoteSessionFactory
        {
            ListingOverride = _ => new RepeatedRemoteEntryList(
                (int)(RemoteDirectorySnapshot.MaximumNameUtf8Bytes / NameLength) + 1,
                ListedFile(new string('n', NameLength))),
        };
        var provider = new SftpFileProvider(sessions, RemoteProviderTestProfiles.SftpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        var result = await provider.ListAsync(
            new FileListRequest(root, 1),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.LimitExceeded, result.Error!.Code);
    }

    [Fact]
    public async Task CancellationStopsHostileListingSnapshotBeforeSorting()
    {
        using var cancellation = new CancellationTokenSource();
        var enumerated = 0;
        var sessions = new FakeRemoteSessionFactory
        {
            ListingOverride = _ => new RepeatedRemoteEntryList(
                RemoteDirectorySnapshot.MaximumEntryCount,
                ListedFile("entry"),
                index =>
                {
                    enumerated++;
                    if (index == 4)
                    {
                        cancellation.Cancel();
                    }
                }),
        };
        var provider = new SftpFileProvider(sessions, RemoteProviderTestProfiles.SftpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        var result = await provider.ListAsync(
            new FileListRequest(root, 1),
            cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(5, enumerated);
    }

    [Theory]
    [InlineData((int)RemoteFileSessionErrorCode.HostKeyUnknown, FilePanelErrorCode.HostKeyUnknown, "file_host_key_unknown")]
    [InlineData((int)RemoteFileSessionErrorCode.HostKeyChanged, FilePanelErrorCode.HostKeyChanged, "file_host_key_changed")]
    public async Task HostKeyFailuresRemainActionableAtFilePanelBoundary(
        int sessionErrorValue,
        FilePanelErrorCode panelError,
        string stableCode)
    {
        var sessions = new FakeRemoteSessionFactory
        {
            OpenError = (RemoteFileSessionErrorCode)sessionErrorValue,
        };
        var provider = new SftpFileProvider(sessions, RemoteProviderTestProfiles.SftpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);
        using var client = new FilePanelClient([
            new FileProviderRegistration("SFTP", FileProviderFamily.Sftp, provider, root),
        ]);
        var profile = Assert.Single(client.Profiles);

        var result = await client.ListAsync(
            new FilePanelListRequest(profile.Root, 10, null, ShowHidden: true),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(panelError, result.Error!.Code);
        Assert.Equal(stableCode, result.Error.StableCode);
    }

    [Fact]
    public async Task ReconnectNeverReplaysAMutation()
    {
        var sessions = new FakeRemoteSessionFactory { FailOpenCount = 1 };
        var options = RemoteProviderTestProfiles.SftpOptions(
            reconnectPolicy: RemoteMetadataReconnectPolicy.RetryOnce);
        var provider = new SftpFileProvider(sessions, options);
        var destination = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root)
            .Child(new FilePathSegment("write.txt"));
        await using var source = new MemoryStream([1, 2, 3], writable: false);

        var result = await provider.WriteAsync(
            new FileWriteRequest(
                destination,
                contentLength: 3,
                bufferSize: 2,
                new FileMutationPrecondition.MustNotExist()),
            source,
            progress: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.IoFailure, result.Error!.Code);
        Assert.Equal(1, sessions.OpenCount);
    }

    [Fact]
    public async Task MoveToTheExactSamePathIsRejectedBeforeOpeningASession()
    {
        var sessions = new FakeRemoteSessionFactory();
        var provider = new SftpFileProvider(sessions, RemoteProviderTestProfiles.SftpOptions());
        var location = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root)
            .Child(new FilePathSegment("same.txt"));

        var result = await provider.TransferAsync(
            new FileTransferRequest(
                location,
                location,
                FileTransferKind.Move,
                bufferSize: 8,
                new FileMutationPrecondition.Any()),
            progress: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.InvalidLocation, result.Error!.Code);
        Assert.Equal(0, sessions.OpenCount);
    }

    [Fact]
    public async Task ConnectionPromptCancellationReturnsATypedCancelledResult()
    {
        var sessions = new FakeRemoteSessionFactory { CancelOpenCount = 1 };
        var provider = new SftpFileProvider(sessions, RemoteProviderTestProfiles.SftpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);

        var result = await provider.StatAsync(new FileStatRequest(root), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(1, sessions.OpenCount);
    }

    [Fact]
    public async Task LinkDeletionIsRejectedWithoutRemovingTheEntry()
    {
        var sessions = new FakeRemoteSessionFactory();
        sessions.SeedLink("/link");
        var provider = new SftpFileProvider(sessions, RemoteProviderTestProfiles.SftpOptions());
        var link = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root)
            .Child(new FilePathSegment("link"));

        var deleted = await provider.DeleteAsync(
            new FileDeleteRequest(link, recursive: false, new FileMutationPrecondition.Any()),
            CancellationToken.None);

        Assert.False(deleted.IsSuccess);
        Assert.Equal(FileProviderErrorCode.LinkNotAllowed, deleted.Error!.Code);
        Assert.True((await provider.StatAsync(new FileStatRequest(link), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task CancelledSourceDeletionAfterMoveCommitReturnsPartialTransfer()
    {
        var provider = new SftpFileProvider(
            new FakeRemoteSessionFactory(),
            RemoteProviderTestProfiles.SftpOptions());
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);
        var sourceLocation = root.Child(new FilePathSegment("source.txt"));
        var destination = root.Child(new FilePathSegment("destination.txt"));
        await using (var source = new MemoryStream([1, 2, 3], writable: false))
        {
            var written = await provider.WriteAsync(
                new FileWriteRequest(
                    sourceLocation,
                    contentLength: 3,
                    bufferSize: 2,
                    new FileMutationPrecondition.MustNotExist()),
                source,
                progress: null,
                CancellationToken.None);
            Assert.True(written.IsSuccess, written.Error?.Message);
        }

        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress<FileTransferProgress>(update =>
        {
            if (update.Stage == FileTransferStage.DeletingSource)
            {
                cancellation.Cancel();
            }
        });

        var moved = await provider.TransferAsync(
            new FileTransferRequest(
                sourceLocation,
                destination,
                FileTransferKind.Move,
                bufferSize: 2,
                new FileMutationPrecondition.MustNotExist()),
            progress,
            cancellation.Token);

        Assert.False(moved.IsSuccess);
        Assert.Equal(FileProviderErrorCode.PartialTransfer, moved.Error!.Code);
        Assert.True((await provider.StatAsync(new FileStatRequest(sourceLocation), CancellationToken.None)).IsSuccess);
        Assert.True((await provider.StatAsync(new FileStatRequest(destination), CancellationToken.None)).IsSuccess);
    }

    private static SshHostKeyCandidate Candidate(byte marker) =>
        new("ssh-ed25519", Convert.ToBase64String(Enumerable.Repeat(marker, 32).ToArray()));

    private static RemoteFileEntry ListedFile(string name) =>
        new(name, FileEntryKind.File, 1, null, $"revision:{name.Length}");

    private sealed class RepeatedRemoteEntryList(
        int count,
        RemoteFileEntry entry,
        Action<int>? onEnumerated = null) : IReadOnlyList<RemoteFileEntry>
    {
        public int Count { get; } = count;

        public RemoteFileEntry this[int index] =>
            index >= 0 && index < Count
                ? entry
                : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<RemoteFileEntry> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                onEnumerated?.Invoke(index);
                yield return entry;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
