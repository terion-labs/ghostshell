using System.Globalization;
using Asura.Application;

namespace Asura.Application.Tests;

public sealed class FilePanelDiscoveryTests
{
    [Fact]
    public async Task SearchDescendsOnDemandWithoutBufferingEverySiblingPage()
    {
        var client = new DiscoveryFilePanelClient(pageSize: 2);
        var nested = client.Root.Child(new FilePanelPathSegment("nested"));
        client.Add(client.Root, "nested", FilePanelEntryKind.Directory);
        client.Add(nested, "first-match.md", FilePanelEntryKind.File);
        for (var index = 0; index < 2000; index++)
        {
            client.Add(client.Root, $"sibling-{index}", FilePanelEntryKind.Directory);
        }

        await using var matches = ((IFilePanelClient)client).SearchAsync(
            new FilePanelSearchRequest(client.Root, ".md", FilePanelDiscoveryScope.Subtree, false),
            CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await matches.MoveNextAsync());
        Assert.Equal("first-match.md", matches.Current.Value!.Name);
        Assert.Equal(2, client.ListCallCount);
    }

    [Fact]
    public async Task TraversalDetectsNonAdjacentContinuationCyclesWithBoundedCheckpoints()
    {
        var client = new DiscoveryFilePanelClient(pageSize: 2) { CycleContinuations = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var matches = ((IFilePanelClient)client).SearchAsync(
            new FilePanelSearchRequest(client.Root, "match", FilePanelDiscoveryScope.Subtree, false),
            timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await matches.MoveNextAsync());
        Assert.False(matches.Current.IsSuccess);
        Assert.Equal("file_list_continuation_cycle", matches.Current.Error!.StableCode);
        Assert.InRange(client.ListCallCount, 3, 8);
    }

    [Fact]
    public async Task SearchTraversesEveryPageAndNestedDirectory()
    {
        var client = new DiscoveryFilePanelClient(pageSize: 2);
        var nested = client.Root.Child(new FilePanelPathSegment("nested"));
        client.Add(client.Root, "alpha.txt", FilePanelEntryKind.File);
        client.Add(client.Root, "beta.txt", FilePanelEntryKind.File);
        client.Add(client.Root, "nested", FilePanelEntryKind.Directory);
        client.Add(nested, "test.md", FilePanelEntryKind.File);
        client.Add(nested, ".hidden.md", FilePanelEntryKind.File, isHidden: true);

        var matches = new List<FilePanelEntry>();
        await foreach (var result in ((IFilePanelClient)client).SearchAsync(
            new FilePanelSearchRequest(
                client.Root,
                ".md",
                FilePanelDiscoveryScope.Subtree,
                showHidden: false),
            CancellationToken.None))
        {
            Assert.True(result.IsSuccess, result.Error?.Message);
            matches.Add(result.Value!);
        }

        var match = Assert.Single(matches);
        Assert.Equal("test.md", match.Name);
        Assert.Equal(nested, match.Location.Parent);
        Assert.Equal(3, client.ListCallCount);
    }

    [Fact]
    public async Task WatchSignalsAfterTheProviderSnapshotChanges()
    {
        var client = new DiscoveryFilePanelClient(pageSize: 2);
        client.Add(client.Root, "before.txt", FilePanelEntryKind.File);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var changes = ((IFilePanelClient)client).WatchAsync(
                new FilePanelWatchRequest(
                    client.Root,
                    FilePanelDiscoveryScope.CurrentDirectory,
                    showHidden: false,
                    TimeSpan.FromMilliseconds(50)),
                timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        Assert.True(await changes.MoveNextAsync());
        Assert.True(changes.Current.IsSuccess);
        Assert.Equal(FilePanelChangeKind.Synchronized, changes.Current.Value!.Kind);

        client.Add(client.Root, "after.txt", FilePanelEntryKind.File);

        Assert.True(await changes.MoveNextAsync());
        Assert.True(changes.Current.IsSuccess);
        Assert.Equal(FilePanelChangeKind.Changed, changes.Current.Value!.Kind);
    }

    private sealed class DiscoveryFilePanelClient : IFilePanelClient
    {
        private readonly Dictionary<FilePanelLocation, List<FilePanelEntry>> _entries = [];

        public DiscoveryFilePanelClient(int pageSize)
        {
            Root = new FilePanelLocation(
                "files.test",
                "test",
                new FilePanelAddress.Hierarchical(FilePanelPath.Root));
            Profiles =
            [
                new FileProviderProfileDescriptor(
                    "files.test",
                    "Test files",
                    FileProviderFamily.Posix,
                    Root,
                    FilePanelCapability.List
                        | FilePanelCapability.Search
                        | FilePanelCapability.Watch
                        | FilePanelCapability.Pagination,
                    pageSize,
                    1024),
            ];
        }

        public FilePanelLocation Root { get; }

        public int ListCallCount { get; private set; }
        public bool CycleContinuations { get; init; }

        public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; }

        public void Add(
            FilePanelLocation parent,
            string name,
            FilePanelEntryKind kind,
            bool isHidden = false)
        {
            var entry = new FilePanelEntry(
                parent.Child(new FilePanelPathSegment(name)),
                name,
                kind,
                kind == FilePanelEntryKind.Directory ? null : 1,
                DateTimeOffset.UtcNow,
                isHidden);
            if (!_entries.TryGetValue(parent.WithVersion(null), out var children))
            {
                children = [];
                _entries.Add(parent.WithVersion(null), children);
            }

            children.Add(entry);
        }

        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
            FilePanelListRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCallCount++;
            if (CycleContinuations)
            {
                return ValueTask.FromResult(FilePanelResult<FilePanelPage>.Success(
                    new FilePanelPage([], request.ContinuationToken == "a" ? "b" : "a")));
            }
            var offset = request.ContinuationToken is null
                ? 0
                : int.Parse(request.ContinuationToken, CultureInfo.InvariantCulture);
            var entries = _entries.GetValueOrDefault(request.Location.WithVersion(null), [])
                .Where(entry => request.ShowHidden || !entry.IsHidden)
                .ToArray();
            var page = entries.Skip(offset).Take(request.PageSize).ToArray();
            var nextOffset = offset + page.Length;
            var continuation = nextOffset < entries.Length
                ? nextOffset.ToString(CultureInfo.InvariantCulture)
                : null;
            return ValueTask.FromResult(FilePanelResult<FilePanelPage>.Success(
                new FilePanelPage(page, continuation)));
        }

        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
            FilePanelLocation location,
            CancellationToken cancellationToken) => throw new NotSupportedException();

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
}
