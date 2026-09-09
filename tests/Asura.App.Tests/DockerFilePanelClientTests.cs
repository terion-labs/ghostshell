using System.Text;
using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;
using Asura.Docker;

namespace Asura.App.Tests;

public sealed class DockerFilePanelClientTests
{
    private static readonly DockerResourceReference Resource = new(
        DockerResourceKind.Container,
        "container-api",
        "api");

    [Fact]
    public async Task DockerEntriesUseTheGenericFilePanelContract()
    {
        var docker = new FakeDockerClient
        {
            Entries =
            [
                new DockerFileEntry("etc", "/etc", DockerFileKind.Directory, null, null),
                new DockerFileEntry("readme.txt", "/readme.txt", DockerFileKind.File, 5, null),
                new DockerFileEntry(".dockerenv", "/.dockerenv", DockerFileKind.File, 0, null),
            ],
        };
        var client = new DockerFilePanelClient(docker, BuiltInConnections.Local, Resource);
        var root = Assert.Single(client.Profiles).Root;

        var first = await client.ListAsync(
            new FilePanelListRequest(root, 1, null, ShowHidden: false),
            CancellationToken.None);
        var firstPage = AssertSuccess(first);
        Assert.Equal("etc", Assert.Single(firstPage.Entries).Name);
        Assert.Equal("1", firstPage.ContinuationToken);

        var second = await client.ListAsync(
            new FilePanelListRequest(root, 10, firstPage.ContinuationToken, ShowHidden: false),
            CancellationToken.None);
        Assert.Equal("readme.txt", Assert.Single(AssertSuccess(second).Entries).Name);
    }

    [Fact]
    public async Task DockerPreviewUsesTheSameClassifierAsOtherFileProviders()
    {
        var docker = new FakeDockerClient
        {
            Content = Encoding.UTF8.GetBytes("{\"ready\":true}"),
        };
        var client = new DockerFilePanelClient(docker, BuiltInConnections.Local, Resource);
        var file = client.Profiles[0].Root.Child(new FilePanelPathSegment("status.json"));

        var result = await client.PreviewAsync(
            new FilePanelPreviewRequest(file, 256),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var preview = result.Value!;
        Assert.Equal(FilePanelPreviewKind.StructuredText, preview.Kind);
        Assert.Equal("application/json", preview.MediaType);
        Assert.Equal("/status.json", docker.ReadPath);
    }

    [Fact]
    public async Task DockerStatUsesTheProtocolAdapterInsteadOfEnumeratingTheParent()
    {
        var expected = new DockerFileEntry(
            "status.json",
            "/status.json",
            DockerFileKind.File,
            14,
            null);
        var docker = new FakeDockerClient { StatEntry = expected };
        var client = new DockerFilePanelClient(docker, BuiltInConnections.Local, Resource);
        var file = client.Profiles[0].Root.Child(new FilePanelPathSegment("status.json"));

        var result = await client.StatAsync(file, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("status.json", result.Value!.Name);
        Assert.Equal("/status.json", docker.StatPath);
        Assert.Equal(0, docker.ListCallCount);
    }

    [Fact]
    public async Task LargeDockerPreviewWaitsForExplicitHostTransfer()
    {
        var docker = new FakeDockerClient
        {
            Entries =
            [
                new DockerFileEntry(
                    "database.db",
                    "/database.db",
                    DockerFileKind.File,
                    FileRuntimePanelViewModel.AutoDownloadPreviewBytes + 1,
                    null),
            ],
        };
        var client = new DockerFilePanelClient(docker, BuiltInConnections.Local, Resource);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker files",
            client);
        await panel.Initialization;

        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();

        Assert.True(panel.RequiresHostTransferForPreview);
        Assert.True(panel.ShowPreviewDownloadPrompt);
        Assert.Null(docker.ReadPath);

        await panel.PreviewDeferredAsync();

        Assert.False(panel.ShowPreviewDownloadPrompt);
        Assert.Equal("/database.db", docker.ReadPath);
    }

    [Fact]
    public async Task DockerPreviewToggleDefersEvenASmallFile()
    {
        var docker = new FakeDockerClient
        {
            Entries =
            [
                new DockerFileEntry(
                    "small.txt",
                    "/small.txt",
                    DockerFileKind.File,
                    128,
                    null),
            ],
        };
        var client = new DockerFilePanelClient(docker, BuiltInConnections.Local, Resource);
        using var panel = new FileRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Docker files",
            client);
        await panel.Initialization;
        panel.AutoDownloadPreviews = false;

        panel.SelectedEntry = Assert.Single(panel.Entries);
        await panel.PreviewSelectedAsync();

        Assert.True(panel.RequiresHostTransferForPreview);
        Assert.True(panel.ShowPreviewDownloadPrompt);
        Assert.Null(docker.ReadPath);
    }

    private static FilePanelPage AssertSuccess(FilePanelResult<FilePanelPage> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    private sealed class FakeDockerClient : IDockerEngineClient
    {
        public IReadOnlyList<DockerFileEntry> Entries { get; init; } = [];

        public ReadOnlyMemory<byte> Content { get; init; } = ReadOnlyMemory<byte>.Empty;

        public DockerFileEntry? StatEntry { get; init; }

        public string? ReadPath { get; private set; }

        public string? StatPath { get; private set; }

        public int ListCallCount { get; private set; }

        public ValueTask<DockerResult<DockerFileListing>> ListFilesAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            string path,
            CancellationToken cancellationToken)
        {
            ListCallCount++;
            return ValueTask.FromResult<DockerResult<DockerFileListing>>(
                new DockerResult<DockerFileListing>.Success(
                    new DockerFileListing(resource, path, Entries)));
        }

        public ValueTask<DockerResult<DockerFileEntry>> StatFileAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            string path,
            CancellationToken cancellationToken)
        {
            StatPath = path;
            return ValueTask.FromResult<DockerResult<DockerFileEntry>>(
                StatEntry is { } entry
                    ? new DockerResult<DockerFileEntry>.Success(entry)
                    : new DockerResult<DockerFileEntry>.Failure(new DockerError(
                        DockerErrorCode.FileNotFound,
                        "File not found.",
                        false)));
        }

        public ValueTask<DockerResult<DockerFileContent>> ReadFileAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            string path,
            long maximumBytes,
            CancellationToken cancellationToken)
        {
            ReadPath = path;
            return ValueTask.FromResult<DockerResult<DockerFileContent>>(
                new DockerResult<DockerFileContent>.Success(new DockerFileContent(
                    resource,
                    path,
                    Content[..(int)Math.Min(Content.Length, maximumBytes)],
                    Content.Length > maximumBytes)));
        }

        public ValueTask<DockerResult<DockerEngineSnapshot>> ReadSnapshotAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DockerResult<IReadOnlyList<DockerVolumeUsage>>> ReadVolumeUsageAsync(
            ConnectionProfile connection,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DockerResult<DockerResourceInspection>> InspectAsync(
            ConnectionProfile connection,
            DockerResourceReference resource,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DockerResult<DockerContainerLogPage>> ReadContainerLogsAsync(
            ConnectionProfile connection,
            DockerContainerLogRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DockerResult<bool>> DownloadContainerLogsAsync(
            ConnectionProfile connection,
            string containerId,
            Stream destination,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DockerResult<string>> ResolveContainerShellAsync(
            ConnectionProfile connection,
            string containerId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<DockerResult<bool>> RunContainerActionAsync(
            ConnectionProfile connection,
            string containerId,
            DockerContainerAction action,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
