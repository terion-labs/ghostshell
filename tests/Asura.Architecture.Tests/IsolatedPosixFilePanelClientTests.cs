using System.Text;
using Asura.Application;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class IsolatedPosixFilePanelClientTests
{
    [Fact]
    public async Task InvalidUtf8FileNamesRemainAddressableByTheirOriginalBytes()
    {
        byte[] fileName = [0xcb, (byte)'7', (byte)'_', 0x0e, (byte)'8', (byte)'h', (byte)'o'];
        var executor = new RecordingExecutor
        {
            Listing = $"{Convert.ToBase64String(fileName)}\tf\t0\t0\n",
        };
        var client = new IsolatedPosixFilePanelClient(executor);
        var profile = Assert.Single(client.Profiles);
        Assert.Equal("Workspace", profile.Name);
        var root = profile.StartLocation!;
        var startPath = Assert.IsType<FilePanelAddress.Hierarchical>(root.Address).Path;
        Assert.Equal(
            ["home", "asura"],
            startPath.Segments.Select(segment => segment.Value),
            StringComparer.Ordinal);

        var listed = await client.ListAsync(
            new FilePanelListRequest(root, 100, null, ShowHidden: true),
            CancellationToken.None);
        var entry = Assert.Single(AssertSuccess(listed).Entries);
        Assert.Equal("\\xCB7_\\x0E8ho", entry.Name);

        var preview = await client.PreviewAsync(
            new FilePanelPreviewRequest(entry.Location, 16),
            CancellationToken.None);
        Assert.True(preview.IsSuccess);
        var encodedPath = Assert.IsType<ConnectionBinaryCommand>(executor.LastBinaryCommand)
            .Arguments[3];
        Assert.Equal(
            [.. Encoding.UTF8.GetBytes("/home/asura/"), .. fileName],
            Convert.FromBase64String(encodedPath));
    }

    [Fact]
    public async Task ReservedPrefixFileNamesRoundTripWithoutChangingTheirMeaning()
    {
        byte[] fileName = Encoding.UTF8.GetBytes("asura-posix:Li4vZXRjL3Bhc3N3ZA");
        var executor = new RecordingExecutor
        {
            Listing = $"{Convert.ToBase64String(fileName)}\tf\t0\t0\n",
        };
        var client = new IsolatedPosixFilePanelClient(executor);
        var root = client.Profiles.Single().StartLocation!;

        var listed = await client.ListAsync(
            new FilePanelListRequest(root, 100, null, ShowHidden: true),
            CancellationToken.None);
        var entry = Assert.Single(AssertSuccess(listed).Entries);
        var address = Assert.IsType<FilePanelAddress.Hierarchical>(entry.Location.Address);
        Assert.NotEqual(
            Encoding.UTF8.GetString(fileName),
            address.Path.Segments[^1].Value,
            StringComparer.Ordinal);

        var preview = await client.PreviewAsync(
            new FilePanelPreviewRequest(entry.Location, 16),
            CancellationToken.None);

        Assert.True(preview.IsSuccess);
        var encodedPath = Assert.IsType<ConnectionBinaryCommand>(executor.LastBinaryCommand)
            .Arguments[3];
        Assert.Equal(
            [.. Encoding.UTF8.GetBytes("/home/asura/"), .. fileName],
            Convert.FromBase64String(encodedPath));
    }

    [Fact]
    public async Task EncodedTraversalSegmentIsRejectedBeforeExecution()
    {
        var executor = new RecordingExecutor();
        var client = new IsolatedPosixFilePanelClient(executor);
        var root = client.Profiles.Single().StartLocation!;
        var traversal = root.Child(new FilePanelPathSegment(
            "asura-posix:Li4vZXRjL3Bhc3N3ZA"));

        var preview = await client.PreviewAsync(
            new FilePanelPreviewRequest(traversal, 16),
            CancellationToken.None);

        Assert.False(preview.IsSuccess);
        Assert.Equal(FilePanelErrorCode.InvalidLocation, preview.Error!.Code);
        Assert.Null(executor.LastBinaryCommand);
    }

    [Fact]
    public async Task WorkspaceRootCannotBeMutated()
    {
        var executor = new RecordingExecutor();
        var client = new IsolatedPosixFilePanelClient(executor);
        var root = client.Profiles.Single().Root;
        var child = root.Child(new FilePanelPathSegment("child"));

        var results = new FilePanelError?[]
        {
            (await client.CreateDirectoryAsync(
                new FilePanelCreateDirectoryRequest(
                    root,
                    FilePanelMutationPrecondition.MustNotExist),
                CancellationToken.None)).Error,
            (await client.RenameAsync(
                new FilePanelRenameRequest(
                    root,
                    child,
                    FilePanelMutationPrecondition.MustNotExist),
                CancellationToken.None)).Error,
            (await client.DeleteAsync(
                new FilePanelDeleteRequest(
                    root,
                    Recursive: true,
                    FilePanelMutationPrecondition.MustExist),
                CancellationToken.None)).Error,
            (await client.WriteTextAsync(
                new FilePanelTextWriteRequest(
                    root,
                    "content",
                    FilePanelMutationPrecondition.Any),
                CancellationToken.None)).Error,
            (await client.CopyAsync(
                new FilePanelCopyRequest(child, root, 1),
                CancellationToken.None)).Error,
        };

        Assert.All(results, error =>
            Assert.Equal(FilePanelErrorCode.RootMutationNotAllowed, error!.Code));
        Assert.Null(executor.LastBinaryCommand);
        Assert.Equal(0, executor.CommandCount);
    }

    private static T AssertSuccess<T>(FilePanelResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    private sealed class RecordingExecutor : IConnectionCommandExecutor
    {
        public string Listing { get; init; } = string.Empty;

        public ConnectionBinaryCommand? LastBinaryCommand { get; private set; }

        public int CommandCount { get; private set; }

        public ValueTask<ConnectionCommandResult> ExecuteAsync(
            ConnectionCommand request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandCount++;
            return ValueTask.FromResult(new ConnectionCommandResult(
                ConnectionCommandOutcome.Exited,
                0,
                Listing));
        }

        public ValueTask<ConnectionBinaryCommandResult> ExecuteBinaryAsync(
            ConnectionBinaryCommand request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastBinaryCommand = request;
            return ValueTask.FromResult(new ConnectionBinaryCommandResult(
                ConnectionCommandOutcome.Exited,
                0,
                ReadOnlyMemory<byte>.Empty));
        }

        public ValueTask<ConnectionStreamingCommandResult<T>> ExecuteStreamingAsync<T>(
            ConnectionBinaryCommand request,
            Func<Stream, CancellationToken, ValueTask<T>> consumeOutput,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
