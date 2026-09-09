using System.Text;

namespace Asura.Files.Tests;

/// <summary>
/// Common behavioral contract for every file-provider adapter. A provider may only advertise
/// a capability after this suite contains and passes its corresponding behavior test.
/// </summary>
public abstract class FileProviderConformanceSuite
{
    private const FileProviderCapability RequiredCapabilities =
        FileProviderCapability.List
        | FileProviderCapability.Stat
        | FileProviderCapability.RangedRead
        | FileProviderCapability.StreamingWrite;

    private const FileProviderCapability CoveredCapabilities =
        FileProviderCapability.List
        | FileProviderCapability.Stat
        | FileProviderCapability.RangedRead
        | FileProviderCapability.StreamingWrite
        | FileProviderCapability.CreateDirectory
        | FileProviderCapability.Rename
        | FileProviderCapability.Copy
        | FileProviderCapability.Move
        | FileProviderCapability.Delete
        | FileProviderCapability.AtomicReplace
        | FileProviderCapability.ServerSideCopy
        | FileProviderCapability.Permissions
        | FileProviderCapability.AccessControlLists
        | FileProviderCapability.Pagination;

    protected abstract ValueTask<FileProviderTestContext> CreateContextAsync();

    [Fact]
    public async Task DeclaredCapabilitiesAreCoveredByTheConformanceSuite()
    {
        await using var context = await CreateContextAsync();

        Assert.Equal(
            RequiredCapabilities,
            context.Provider.Capabilities.Supported & RequiredCapabilities);
        Assert.Equal(
            FileProviderCapability.None,
            context.Provider.Capabilities.Supported & ~CoveredCapabilities);
        if (context.Provider.Capabilities.Supports(FileProviderCapability.ServerSideCopy))
        {
            Assert.True(context.Provider.Capabilities.Supports(FileProviderCapability.Copy));
            Assert.True(
                context.CanObserveServerSideCopy,
                "ServerSideCopy requires an adapter-level conformance probe; a successful copy alone is insufficient.");
        }
    }

    /// <summary>
    /// A provider that declares it can describe who has access must actually
    /// answer, and must answer in the one shape it has: nine bits or a list of
    /// grants, never both and never neither. A capability declared without an
    /// implementation behind it is worse than one not declared — the shell puts
    /// a Permissions entry in the menu and the connection refuses it.
    /// </summary>
    [Fact]
    public async Task DeclaredAccessControlIsAnsweredInExactlyOneShape()
    {
        await using var context = await CreateContextAsync();
        var permissions = context.Provider.Capabilities.Supports(
            FileProviderCapability.Permissions);
        var grants = context.Provider.Capabilities.Supports(
            FileProviderCapability.AccessControlLists);
        if (!permissions && !grants)
        {
            return;
        }

        Assert.False(
            permissions && grants,
            "A connection describes access one way or the other, not both.");

        var location = context.Root.Child(new FilePathSegment("access-control.txt"));
        await WriteBytesAsync(
            context.Provider,
            location,
            Encoding.UTF8.GetBytes("who goes there"),
            new FileMutationPrecondition.MustNotExist());

        var read = await context.Provider.GetAccessControlAsync(
            new FileAccessControlRequest(location),
            CancellationToken.None);
        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(permissions, read.Value!.Mode is not null);
        Assert.Equal(grants, read.Value.Grants.Count > 0);

        // And what is written comes back, so the change reached the connection
        // rather than only the dialog that asked for it.
        if (permissions)
        {
            var written = await context.Provider.SetAccessControlAsync(
                new FileSetAccessControlRequest(
                    location,
                    mode: new Asura.Application.FilePanelPosixMode(0b110_000_000)),
                CancellationToken.None);
            Assert.True(written.IsSuccess, written.Error?.Message);
            Assert.Equal("600", written.Value!.Mode!.Octal);
        }
        else
        {
            var written = await context.Provider.SetAccessControlAsync(
                new FileSetAccessControlRequest(
                    location,
                    grants:
                    [
                        new Asura.Application.FilePanelAccessGrant(
                            new Asura.Application.FilePanelGrantee(
                                Asura.Application.FilePanelGranteeKind.Everyone),
                            Asura.Application.FilePanelAccessRight.Read),
                    ]),
                CancellationToken.None);
            Assert.True(written.IsSuccess, written.Error?.Message);
            Assert.Contains(
                written.Value!.Grants,
                grant => grant.Grantee.Kind
                    == Asura.Application.FilePanelGranteeKind.Everyone);
        }
    }

    [Fact]
    public async Task ListStatAndPaginationPreserveStructuredLocations()
    {
        await using var context = await CreateContextAsync();
        var directory = context.Root;

        for (var index = 0; index < 5; index++)
        {
            await WriteBytesAsync(
                context.Provider,
                directory.Child(new FilePathSegment($"file-{index}.txt")),
                Encoding.UTF8.GetBytes($"item-{index}"),
                new FileMutationPrecondition.MustNotExist());
        }

        var listedNames = new HashSet<string>(StringComparer.Ordinal);
        var supportsPagination = context.Provider.Capabilities.Supports(FileProviderCapability.Pagination);
        var pageSize = supportsPagination
            ? 2
            : Math.Min(10, context.Provider.Capabilities.Limits.MaximumListPageSize);
        FilePageToken? continuation = null;
        do
        {
            var page = await context.Provider.ListAsync(
                new FileListRequest(directory, pageSize, continuation),
                CancellationToken.None);
            Assert.True(page.IsSuccess, page.Error?.Message);
            Assert.InRange(page.Value!.Items.Length, 1, pageSize);
            foreach (var item in page.Value.Items)
            {
                Assert.Equal(context.Provider.ProfileId, item.Location.ProviderProfileId);
                Assert.Equal(directory.Path, item.Location.Path.Parent);
                Assert.NotNull(item.Location.Version);
                listedNames.Add(item.Location.Path.Name!.Value.Value);
            }

            continuation = page.Value.ContinuationToken;
        }
        while (continuation is not null);

        Assert.Equal(5, listedNames.Count);
        var stat = await context.Provider.StatAsync(
            new FileStatRequest(directory.Child(new FilePathSegment("file-0.txt"))),
            CancellationToken.None);
        Assert.True(stat.IsSuccess, stat.Error?.Message);
        Assert.Equal(FileEntryKind.File, stat.Value!.Kind);
        Assert.Equal(6, stat.Value.Size);
    }

    [Fact]
    public async Task DeclaredCreateDirectoryCapabilityCreatesAListableDirectory()
    {
        await using var context = await CreateContextAsync();
        if (!context.Provider.Capabilities.Supports(FileProviderCapability.CreateDirectory))
        {
            return;
        }

        var directory = context.Root.Child(new FilePathSegment("created-directory"));
        var created = await context.Provider.CreateDirectoryAsync(
            new FileCreateDirectoryRequest(
                directory,
                new FileMutationPrecondition.MustNotExist()),
            CancellationToken.None);

        Assert.True(created.IsSuccess, created.Error?.Message);
        Assert.Equal(FileEntryKind.Directory, created.Value!.Kind);
        var stat = await context.Provider.StatAsync(new FileStatRequest(directory), CancellationToken.None);
        Assert.True(stat.IsSuccess, stat.Error?.Message);
        Assert.Equal(FileEntryKind.Directory, stat.Value!.Kind);
    }

    [Fact]
    public async Task RangedReadNeverExceedsTheRequestedBound()
    {
        await using var context = await CreateContextAsync();
        var location = context.Root.Child(new FilePathSegment("bounded.bin"));
        await WriteBytesAsync(
            context.Provider,
            location,
            [.. Enumerable.Range(0, 10).Select(value => (byte)value)],
            new FileMutationPrecondition.MustNotExist());

        await using var destination = new MemoryStream();
        var result = await context.Provider.ReadAsync(
            new FileReadRequest(location, offset: 2, maximumBytes: 4, bufferSize: 2),
            destination,
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(new byte[] { 2, 3, 4, 5 }, destination.ToArray());
        Assert.Equal(4, result.Value!.BytesRead);
        Assert.True(result.Value.IsTruncated);
    }

    [Fact]
    public async Task VersionConflictsAreTypedAndAtomicReplacePreservesTheWinner()
    {
        await using var context = await CreateContextAsync();
        var location = context.Root.Child(new FilePathSegment("versioned.txt"));
        await WriteBytesAsync(
            context.Provider,
            location,
            Encoding.UTF8.GetBytes("first"),
            new FileMutationPrecondition.MustNotExist());
        var firstStat = await context.Provider.StatAsync(
            new FileStatRequest(location),
            CancellationToken.None);
        Assert.True(firstStat.IsSuccess);

        var collision = await WriteResultAsync(
            context.Provider,
            location,
            "collision",
            new FileMutationPrecondition.MustNotExist());
        AssertError(collision, FileProviderErrorCode.Conflict);

        var replaced = await WriteResultAsync(
            context.Provider,
            location,
            "second-version",
            new FileMutationPrecondition.VersionMatches(firstStat.Value!.Version));
        Assert.True(replaced.IsSuccess, replaced.Error?.Message);

        var stale = await WriteResultAsync(
            context.Provider,
            location,
            "stale-writer",
            new FileMutationPrecondition.VersionMatches(firstStat.Value.Version));
        AssertError(stale, FileProviderErrorCode.PreconditionFailed);
        Assert.Equal("second-version", await ReadTextAsync(context.Provider, location));
    }

    [Fact]
    public async Task CancelledWriteReturnsTypedFailureAndDoesNotExposePartialDestinationContent()
    {
        await using var context = await CreateContextAsync();
        var location = context.Root.Child(new FilePathSegment("stable.txt"));
        await WriteBytesAsync(
            context.Provider,
            location,
            Encoding.UTF8.GetBytes("stable"),
            new FileMutationPrecondition.MustNotExist());
        var stat = await context.Provider.StatAsync(new FileStatRequest(location), CancellationToken.None);
        Assert.True(stat.IsSuccess);

        using var cancellation = new CancellationTokenSource();
        await using var source = new CancellingReadStream(cancellation, 32);
        var result = await context.Provider.WriteAsync(
            new FileWriteRequest(
                location,
                contentLength: 32,
                bufferSize: 4,
                new FileMutationPrecondition.VersionMatches(stat.Value!.Version)),
            source,
            progress: null,
            cancellation.Token);

        AssertError(result, FileProviderErrorCode.Cancelled);
        Assert.Equal("stable", await ReadTextAsync(context.Provider, location));
    }

    [Fact]
    public async Task DeclaredCopyCapabilityCopiesWithoutDeletingTheSource()
    {
        await using var context = await CreateContextAsync();
        if (!context.Provider.Capabilities.Supports(FileProviderCapability.Copy))
        {
            return;
        }

        var source = context.Root.Child(new FilePathSegment("source.txt"));
        var copy = context.Root.Child(new FilePathSegment("copy.txt"));
        await WriteBytesAsync(
            context.Provider,
            source,
            Encoding.UTF8.GetBytes("payload"),
            new FileMutationPrecondition.MustNotExist());

        var copyResult = await context.Provider.TransferAsync(
            new FileTransferRequest(
                source,
                copy,
                FileTransferKind.Copy,
                bufferSize: 3,
                new FileMutationPrecondition.MustNotExist()),
            progress: null,
            CancellationToken.None);
        Assert.True(copyResult.IsSuccess, copyResult.Error?.Message);
        Assert.False(copyResult.Value!.SourceDeleted);
        if (context.Provider.Capabilities.Supports(FileProviderCapability.ServerSideCopy))
        {
            await context.AssertServerSideCopyObservedAsync();
        }

        Assert.Equal("payload", await ReadTextAsync(context.Provider, source));
        Assert.Equal("payload", await ReadTextAsync(context.Provider, copy));
    }

    [Fact]
    public async Task TransferStreamsWithoutARequestSizeCeiling()
    {
        await using var context = await CreateContextAsync();
        if (!context.Provider.Capabilities.Supports(FileProviderCapability.Copy))
        {
            return;
        }

        var source = context.Root.Child(new FilePathSegment("large-source.bin"));
        var destination = context.Root.Child(new FilePathSegment("streamed-copy.bin"));
        await WriteBytesAsync(
            context.Provider,
            source,
            [.. Enumerable.Range(0, 32).Select(value => (byte)value)],
            new FileMutationPrecondition.MustNotExist());

        var result = await context.Provider.TransferAsync(
            new FileTransferRequest(
                source,
                destination,
                FileTransferKind.Copy,
                bufferSize: 4,
                new FileMutationPrecondition.MustNotExist()),
            progress: null,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var destinationEntry = await context.Provider.StatAsync(
            new FileStatRequest(destination),
            CancellationToken.None);
        Assert.True(destinationEntry.IsSuccess, destinationEntry.Error?.Message);
        Assert.Equal(32, destinationEntry.Value!.Size);
    }

    [Fact]
    public async Task CancelledAtomicTransferLeavesTheDestinationUnchanged()
    {
        await using var context = await CreateContextAsync();
        var required = FileProviderCapability.Copy | FileProviderCapability.AtomicReplace;
        if (!context.Provider.Capabilities.Supports(required))
        {
            return;
        }

        var source = context.Root.Child(new FilePathSegment("cancel-source.bin"));
        var destination = context.Root.Child(new FilePathSegment("cancel-destination.bin"));
        await WriteBytesAsync(
            context.Provider,
            source,
            [.. Enumerable.Range(0, 1_024).Select(value => (byte)(value % 251))],
            new FileMutationPrecondition.MustNotExist());
        await WriteBytesAsync(
            context.Provider,
            destination,
            Encoding.UTF8.GetBytes("stable"),
            new FileMutationPrecondition.MustNotExist());

        using var cancellation = new CancellationTokenSource();
        var progress = new SynchronousProgress<FileTransferProgress>(update =>
        {
            if (update.BytesTransferred > 0)
            {
                cancellation.Cancel();
            }
        });
        var result = await context.Provider.TransferAsync(
            new FileTransferRequest(
                source,
                destination,
                FileTransferKind.Copy,
                bufferSize: 16,
                new FileMutationPrecondition.Any()),
            progress,
            cancellation.Token);

        AssertError(result, FileProviderErrorCode.Cancelled);
        Assert.Equal("stable", await ReadTextAsync(context.Provider, destination));
    }

    [Fact]
    public async Task DeclaredMoveCapabilityCopiesThenDeletesTheSource()
    {
        await using var context = await CreateContextAsync();
        if (!context.Provider.Capabilities.Supports(FileProviderCapability.Move))
        {
            return;
        }

        var source = context.Root.Child(new FilePathSegment("move-source.txt"));
        var moved = context.Root.Child(new FilePathSegment("moved.txt"));
        await WriteBytesAsync(
            context.Provider,
            source,
            Encoding.UTF8.GetBytes("payload"),
            new FileMutationPrecondition.MustNotExist());

        var moveResult = await context.Provider.TransferAsync(
            new FileTransferRequest(
                source,
                moved,
                FileTransferKind.Move,
                bufferSize: 3,
                new FileMutationPrecondition.MustNotExist()),
            progress: null,
            CancellationToken.None);
        Assert.True(moveResult.IsSuccess, moveResult.Error?.Message);
        Assert.True(moveResult.Value!.SourceDeleted);
        AssertError(
            await context.Provider.StatAsync(new FileStatRequest(source), CancellationToken.None),
            FileProviderErrorCode.NotFound);
        Assert.Equal("payload", await ReadTextAsync(context.Provider, moved));
    }

    [Fact]
    public async Task DeclaredRenameCapabilityMovesTheEntryDirectly()
    {
        await using var context = await CreateContextAsync();
        if (!context.Provider.Capabilities.Supports(FileProviderCapability.Rename))
        {
            return;
        }

        var source = context.Root.Child(new FilePathSegment("rename-source.txt"));
        var renamed = context.Root.Child(new FilePathSegment("renamed.txt"));
        await WriteBytesAsync(
            context.Provider,
            source,
            Encoding.UTF8.GetBytes("payload"),
            new FileMutationPrecondition.MustNotExist());

        var renameResult = await context.Provider.RenameAsync(
            new FileRenameRequest(
                source,
                renamed,
                new FileMutationPrecondition.MustNotExist()),
            CancellationToken.None);
        Assert.True(renameResult.IsSuccess, renameResult.Error?.Message);
        AssertError(
            await context.Provider.StatAsync(new FileStatRequest(source), CancellationToken.None),
            FileProviderErrorCode.NotFound);
        Assert.Equal("payload", await ReadTextAsync(context.Provider, renamed));
    }

    [Fact]
    public async Task DeclaredDeleteCapabilityRemovesTheEntry()
    {
        await using var context = await CreateContextAsync();
        if (!context.Provider.Capabilities.Supports(FileProviderCapability.Delete))
        {
            return;
        }

        var location = context.Root.Child(new FilePathSegment("delete-me.txt"));
        await WriteBytesAsync(
            context.Provider,
            location,
            Encoding.UTF8.GetBytes("payload"),
            new FileMutationPrecondition.MustNotExist());

        var deleteResult = await context.Provider.DeleteAsync(
            new FileDeleteRequest(
                location,
                recursive: false,
                new FileMutationPrecondition.Any()),
            CancellationToken.None);
        Assert.True(deleteResult.IsSuccess, deleteResult.Error?.Message);
        AssertError(
            await context.Provider.StatAsync(new FileStatRequest(location), CancellationToken.None),
            FileProviderErrorCode.NotFound);
    }

    [Fact]
    public async Task LimitsAndFilesystemFailuresUseStableErrorCodes()
    {
        await using var context = await CreateContextAsync();
        var missing = context.Root.Child(new FilePathSegment("missing.txt"));
        AssertError(
            await context.Provider.StatAsync(new FileStatRequest(missing), CancellationToken.None),
            FileProviderErrorCode.NotFound);

        await using var oversizedDestination = new MemoryStream();
        var oversizedRead = await context.Provider.ReadAsync(
            new FileReadRequest(
                missing,
                offset: 0,
                maximumBytes: context.Provider.Capabilities.Limits.MaximumReadBytes + 1,
                bufferSize: 1),
            oversizedDestination,
            progress: null,
            CancellationToken.None);
        AssertError(oversizedRead, FileProviderErrorCode.LimitExceeded);

        await using var directoryDestination = new MemoryStream();
        var directoryRead = await context.Provider.ReadAsync(
            new FileReadRequest(context.Root, offset: 0, maximumBytes: 1, bufferSize: 1),
            directoryDestination,
            progress: null,
            CancellationToken.None);
        AssertError(directoryRead, FileProviderErrorCode.IsDirectory);

        var wrongProfile = new FileLocation(
            new FileProviderProfileId("another-profile"),
            context.Root.Authority,
            FilePath.Root);
        AssertError(
            await context.Provider.StatAsync(new FileStatRequest(wrongProfile), CancellationToken.None),
            FileProviderErrorCode.InvalidLocation);
    }

    private static async ValueTask WriteBytesAsync(
        IFileProvider provider,
        FileLocation location,
        byte[] content,
        FileMutationPrecondition precondition)
    {
        await using var source = new MemoryStream(content, writable: false);
        var result = await provider.WriteAsync(
            new FileWriteRequest(location, content.Length, bufferSize: 4, precondition),
            source,
            progress: null,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    private static async ValueTask<FileProviderResult<FileWriteReceipt>> WriteResultAsync(
        IFileProvider provider,
        FileLocation location,
        string content,
        FileMutationPrecondition precondition)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var source = new MemoryStream(bytes, writable: false);
        return await provider.WriteAsync(
            new FileWriteRequest(location, bytes.Length, bufferSize: 4, precondition),
            source,
            progress: null,
            CancellationToken.None);
    }

    private static async ValueTask<string> ReadTextAsync(
        IFileProvider provider,
        FileLocation location)
    {
        await using var destination = new MemoryStream();
        var result = await provider.ReadAsync(
            new FileReadRequest(location, 0, maximumBytes: 1_024, bufferSize: 4),
            destination,
            progress: null,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return Encoding.UTF8.GetString(destination.ToArray());
    }

    private static void AssertError<T>(
        FileProviderResult<T> result,
        FileProviderErrorCode expectedCode)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.Error!.Code);
        Assert.False(string.IsNullOrWhiteSpace(result.Error.StableCode));
    }
}
