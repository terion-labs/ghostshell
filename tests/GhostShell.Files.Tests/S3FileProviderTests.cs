using System.Text;

namespace GhostShell.Files.Tests;

public sealed class S3FileProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_remote_tokens_are_rejected_even_when_local_tokens_are_unique(bool cycle)
    {
        var store = new FakeS3ObjectStore
        {
            ListingOverride = token => new S3ObjectPage([], [], true,
                token is null || !cycle || token == "second" ? "first" : "second"),
        };
        var provider = CreateProvider(store);
        var root = new FileLocation(ProfileId, Authority, FilePath.Root);
        var page = await provider.ListAsync(new FileListRequest(root, 1), CancellationToken.None);
        Assert.True(page.IsSuccess);
        if (cycle)
        {
            page = await provider.ListAsync(new FileListRequest(root, 1, page.Value!.ContinuationToken), CancellationToken.None);
            Assert.True(page.IsSuccess);
        }
        var repeated = await provider.ListAsync(new FileListRequest(root, 1, page.Value!.ContinuationToken), CancellationToken.None);
        Assert.False(repeated.IsSuccess);
        Assert.Equal(FileProviderErrorCode.IoFailure, repeated.Error!.Code);
    }

    private static readonly FileProviderProfileId ProfileId = new("s3-tests");
    private static readonly FileAuthority Authority = new("bucket");

    [Fact]
    public async Task ExactObjectKeysRemainOpaqueAcrossWriteStatAndRead()
    {
        var (provider, store) = CreateProvider();
        var location = FileLocation.ForObjectKey(
            ProfileId,
            Authority,
            new FileObjectKey("folder//./../name/"));
        var content = Encoding.UTF8.GetBytes("opaque-key");

        var write = await WriteAsync(provider, location, content);
        Assert.True(write.IsSuccess, write.Error?.Message);
        Assert.True(store.Contains("folder//./../name/"));
        Assert.Equal(location.ObjectKey, write.Value!.Destination.Location.ObjectKey);

        var stat = await provider.StatAsync(new FileStatRequest(location), CancellationToken.None);
        Assert.True(stat.IsSuccess, stat.Error?.Message);
        Assert.Equal(location.ObjectKey, stat.Value!.Location.ObjectKey);

        await using var destination = new MemoryStream();
        var read = await provider.ReadAsync(
            new FileReadRequest(location, 0, 100, 4),
            destination,
            progress: null,
            CancellationToken.None);
        Assert.True(read.IsSuccess, read.Error?.Message);
        Assert.Equal(content, destination.ToArray());
    }

    [Fact]
    public async Task ProviderTokenBoundsAnOversizedRemoteContinuationToken()
    {
        var store = new FakeS3ObjectStore { ContinuationTokenPadding = 600 };
        var provider = CreateProvider(store);
        var root = new FileLocation(ProfileId, Authority, FilePath.Root);
        await WriteAsync(provider, root.Child(new FilePathSegment("a")), [1]);
        await WriteAsync(provider, root.Child(new FilePathSegment("b")), [2]);

        var first = await provider.ListAsync(
            new FileListRequest(root, pageSize: 1),
            CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.NotNull(first.Value!.ContinuationToken);
        Assert.InRange(first.Value.ContinuationToken!.Value.Value.Length, 1, 256);

        var second = await provider.ListAsync(
            new FileListRequest(root, pageSize: 1, first.Value.ContinuationToken),
            CancellationToken.None);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.NotNull(store.LastContinuationToken);
        Assert.True(store.LastContinuationToken!.Length > 256);
    }

    [Fact]
    public async Task CopyUsesTheServerSidePathAndMoveIsExplicitlyUnsupported()
    {
        var (provider, store) = CreateProvider();
        var root = new FileLocation(ProfileId, Authority, FilePath.Root);
        var source = root.Child(new FilePathSegment("source"));
        var destination = root.Child(new FilePathSegment("destination"));
        await WriteAsync(provider, source, Encoding.UTF8.GetBytes("payload"));
        var readsBeforeCopy = store.ReadCalls;

        var copy = await provider.TransferAsync(
            new FileTransferRequest(
                source,
                destination,
                FileTransferKind.Copy,
                bufferSize: 4,
                new FileMutationPrecondition.MustNotExist()),
            progress: null,
            CancellationToken.None);
        Assert.True(copy.IsSuccess, copy.Error?.Message);
        Assert.Equal(1, store.CopyCalls);
        Assert.Equal(readsBeforeCopy, store.ReadCalls);

        var move = await provider.TransferAsync(
            new FileTransferRequest(
                source,
                root.Child(new FilePathSegment("moved")),
                FileTransferKind.Move,
                bufferSize: 4,
                new FileMutationPrecondition.MustNotExist()),
            progress: null,
            CancellationToken.None);
        Assert.Equal(FileProviderErrorCode.UnsupportedCapability, move.Error!.Code);
    }

    [Fact]
    public async Task ShortUploadIsTypedAndNeverCommitsAnObject()
    {
        var (provider, store) = CreateProvider();
        var location = FileLocation.ForObjectKey(
            ProfileId,
            Authority,
            new FileObjectKey("short"));
        await using var source = new MemoryStream([1, 2]);

        var result = await provider.WriteAsync(
            new FileWriteRequest(
                location,
                contentLength: 3,
                bufferSize: 2,
                new FileMutationPrecondition.MustNotExist()),
            source,
            progress: null,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.UnexpectedEndOfStream, result.Error!.Code);
        Assert.False(store.Contains("short"));
    }

    [Fact]
    public async Task PrefixDeleteNeverExpandsIntoAnUnboundedMultiObjectDelete()
    {
        var (provider, store) = CreateProvider();
        var root = new FileLocation(ProfileId, Authority, FilePath.Root);
        await WriteAsync(
            provider,
            root.Child(new FilePathSegment("folder")).Child(new FilePathSegment("item")),
            [1]);
        var listed = await provider.ListAsync(new FileListRequest(root, 10), CancellationToken.None);
        var folder = Assert.Single(listed.Value!.Items).Location;

        var shallow = await provider.DeleteAsync(
            new FileDeleteRequest(folder, recursive: false, new FileMutationPrecondition.Any()),
            CancellationToken.None);
        Assert.Equal(FileProviderErrorCode.DirectoryNotEmpty, shallow.Error!.Code);

        var recursive = await provider.DeleteAsync(
            new FileDeleteRequest(folder, recursive: true, new FileMutationPrecondition.Any()),
            CancellationToken.None);
        Assert.Equal(FileProviderErrorCode.UnsupportedCapability, recursive.Error!.Code);
        Assert.True(store.Contains("folder/item"));
    }

    [Fact]
    public async Task ObjectKeyMustHaveAnExactUtf8Representation()
    {
        var (provider, _) = CreateProvider();
        var location = FileLocation.ForObjectKey(
            ProfileId,
            Authority,
            new FileObjectKey("invalid-\uD800"));

        var result = await provider.StatAsync(
            new FileStatRequest(location),
            CancellationToken.None);

        Assert.Equal(FileProviderErrorCode.InvalidName, result.Error!.Code);
    }

    private static (S3FileProvider Provider, FakeS3ObjectStore Store) CreateProvider()
    {
        var store = new FakeS3ObjectStore();
        return (CreateProvider(store), store);
    }

    private static S3FileProvider CreateProvider(FakeS3ObjectStore store) =>
        new(store, new S3FileProviderOptions(ProfileId, Authority, "bucket"));

    private static async ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(
        IFileProvider provider,
        FileLocation location,
        byte[] content)
    {
        await using var source = new MemoryStream(content, writable: false);
        return await provider.WriteAsync(
            new FileWriteRequest(
                location,
                content.Length,
                bufferSize: 4,
                new FileMutationPrecondition.MustNotExist()),
            source,
            progress: null,
            CancellationToken.None);
    }
}
