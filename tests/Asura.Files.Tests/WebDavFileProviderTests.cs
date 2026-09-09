namespace Asura.Files.Tests;

public sealed class WebDavFileProviderTests
{
    private static readonly FileProviderProfileId ProfileId = new("webdav-tests");
    private static readonly FileAuthority Authority = new("dav.test");

    [Fact]
    public async Task CopyAndMoveUseDavServerMethodsWithoutClientStreaming()
    {
        using var context = CreateProvider();
        var root = new FileLocation(ProfileId, Authority, FilePath.Root);
        var source = root.Child(new FilePathSegment("source"));
        var copyLocation = root.Child(new FilePathSegment("copy"));
        await WriteAsync(context.Provider, source, [1, 2, 3]);
        var getsBeforeCopy = context.Handler.GetRequests;

        var copy = await context.Provider.TransferAsync(
            new FileTransferRequest(
                source,
                copyLocation,
                FileTransferKind.Copy,
                bufferSize: 2,
                new FileMutationPrecondition.MustNotExist()),
            progress: null,
            CancellationToken.None);
        Assert.True(copy.IsSuccess, copy.Error?.Message);
        Assert.Equal(1, context.Handler.CopyRequests);
        Assert.Equal(getsBeforeCopy, context.Handler.GetRequests);

        var moved = root.Child(new FilePathSegment("moved"));
        var move = await context.Provider.TransferAsync(
            new FileTransferRequest(
                copyLocation,
                moved,
                FileTransferKind.Move,
                bufferSize: 2,
                new FileMutationPrecondition.MustNotExist()),
            progress: null,
            CancellationToken.None);
        Assert.True(move.IsSuccess, move.Error?.Message);
        Assert.True(move.Value!.SourceDeleted);
        Assert.False(context.Handler.Contains("/root/copy"));
        Assert.True(context.Handler.Contains("/root/moved"));
    }

    [Fact]
    public async Task ContinuationPagesUseOneBoundedPropertySnapshot()
    {
        using var context = CreateProvider();
        var root = new FileLocation(ProfileId, Authority, FilePath.Root);
        await WriteAsync(context.Provider, root.Child(new FilePathSegment("a")), [1]);
        await WriteAsync(context.Provider, root.Child(new FilePathSegment("b")), [2]);
        await WriteAsync(context.Provider, root.Child(new FilePathSegment("c")), [3]);

        var first = await context.Provider.ListAsync(
            new FileListRequest(root, 1),
            CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.NotNull(first.Value!.ContinuationToken);
        var second = await context.Provider.ListAsync(
            new FileListRequest(root, 1, first.Value.ContinuationToken),
            CancellationToken.None);
        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Single(second.Value!.Items);
    }

    [Fact]
    public async Task ShallowCollectionDeleteIsRejectedBeforeRecursiveDavDelete()
    {
        using var context = CreateProvider();
        var root = new FileLocation(ProfileId, Authority, FilePath.Root);
        var directory = root.Child(new FilePathSegment("directory"));
        var created = await context.Provider.CreateDirectoryAsync(
            new FileCreateDirectoryRequest(
                directory,
                new FileMutationPrecondition.MustNotExist()),
            CancellationToken.None);
        Assert.True(created.IsSuccess, created.Error?.Message);

        var shallow = await context.Provider.DeleteAsync(
            new FileDeleteRequest(
                directory,
                recursive: false,
                new FileMutationPrecondition.Any()),
            CancellationToken.None);
        Assert.Equal(FileProviderErrorCode.UnsupportedCapability, shallow.Error!.Code);
        Assert.True(context.Handler.Contains("/root/directory"));
    }

    [Fact]
    public async Task ObjectAddressCannotBeSmuggledIntoHierarchicalDavUris()
    {
        using var context = CreateProvider();
        var objectLocation = FileLocation.ForObjectKey(
            ProfileId,
            Authority,
            new FileObjectKey("../outside"));

        var result = await context.Provider.StatAsync(
            new FileStatRequest(objectLocation),
            CancellationToken.None);

        Assert.Equal(FileProviderErrorCode.InvalidLocation, result.Error!.Code);
    }

    [Fact]
    public async Task NonzeroReadRejectsAServerThatIgnoresTheRange()
    {
        using var context = CreateProvider();
        var location = new FileLocation(ProfileId, Authority, FilePath.Root)
            .Child(new FilePathSegment("large.bin"));
        await WriteAsync(context.Provider, location, [1, 2, 3, 4]);
        context.Handler.IgnoreRangeRequests = true;
        await using var destination = new MemoryStream();

        var result = await context.Provider.ReadAsync(
            new FileReadRequest(location, offset: 2, maximumBytes: 1, bufferSize: 1),
            destination,
            progress: null,
            CancellationToken.None);

        Assert.Equal(FileProviderErrorCode.IoFailure, result.Error!.Code);
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task ReadRejectsAPartialResponseForAnotherRange()
    {
        using var context = CreateProvider();
        var location = new FileLocation(ProfileId, Authority, FilePath.Root)
            .Child(new FilePathSegment("range.bin"));
        await WriteAsync(context.Provider, location, [1, 2, 3, 4]);
        context.Handler.ReturnMismatchedContentRange = true;
        await using var destination = new MemoryStream();

        var result = await context.Provider.ReadAsync(
            new FileReadRequest(location, offset: 1, maximumBytes: 2, bufferSize: 1),
            destination,
            progress: null,
            CancellationToken.None);

        Assert.Equal(FileProviderErrorCode.IoFailure, result.Error!.Code);
        Assert.Empty(destination.ToArray());
    }

    [Fact]
    public async Task FileDeleteDoesNotChangeTheResourceUriToACollectionUri()
    {
        using var context = CreateProvider();
        var location = new FileLocation(ProfileId, Authority, FilePath.Root)
            .Child(new FilePathSegment("file.txt"));
        await WriteAsync(context.Provider, location, [1]);

        var result = await context.Provider.DeleteAsync(
            new FileDeleteRequest(
                location,
                recursive: false,
                new FileMutationPrecondition.Any()),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("/root/file.txt", context.Handler.LastDeleteAbsolutePath);
    }

    [Fact]
    public async Task ExistingCollectionMustMatchAnExplicitCreateVersion()
    {
        using var context = CreateProvider();
        var directory = new FileLocation(ProfileId, Authority, FilePath.Root)
            .Child(new FilePathSegment("directory"));
        var created = await context.Provider.CreateDirectoryAsync(
            new FileCreateDirectoryRequest(
                directory,
                new FileMutationPrecondition.MustNotExist()),
            CancellationToken.None);
        Assert.True(created.IsSuccess, created.Error?.Message);

        var result = await context.Provider.CreateDirectoryAsync(
            new FileCreateDirectoryRequest(
                directory,
                new FileMutationPrecondition.VersionMatches(new FileVersion("\"stale\""))),
            CancellationToken.None);

        Assert.Equal(FileProviderErrorCode.PreconditionFailed, result.Error!.Code);
    }

    private static ProviderContext CreateProvider()
    {
        var handler = new FakeWebDavHandler();
        var client = new HttpClient(handler);
        var provider = new WebDavFileProvider(
            client,
            new WebDavFileProviderOptions(
                ProfileId,
                Authority,
                new Uri("https://dav.test/root/")));
        return new ProviderContext(provider, handler, client);
    }

    private static async ValueTask WriteAsync(
        IFileProvider provider,
        FileLocation location,
        byte[] content)
    {
        await using var source = new MemoryStream(content, writable: false);
        var result = await provider.WriteAsync(
            new FileWriteRequest(
                location,
                content.Length,
                bufferSize: 2,
                new FileMutationPrecondition.MustNotExist()),
            source,
            progress: null,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);
    }

    private sealed class ProviderContext(
        WebDavFileProvider provider,
        FakeWebDavHandler handler,
        HttpClient client) : IDisposable
    {
        public WebDavFileProvider Provider { get; } = provider;

        public FakeWebDavHandler Handler { get; } = handler;

        public void Dispose() => client.Dispose();
    }
}
