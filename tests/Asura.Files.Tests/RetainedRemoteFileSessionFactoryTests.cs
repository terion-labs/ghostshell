namespace Asura.Files.Tests;

public sealed class RetainedRemoteFileSessionFactoryTests
{
    [Fact]
    public async Task SequentialLeasesReuseOneHealthyAuthenticatedSession()
    {
        var inner = new RecordingSessionFactory();
        using var sessions = new RetainedRemoteFileSessionFactory(inner);

        await using (var first = await sessions.OpenAsync(CancellationToken.None))
        {
            Assert.True(first.StatDetectsAnyLinkInPath);
            await first.StatAsync("/", CancellationToken.None);
        }

        await using (var second = await sessions.OpenAsync(CancellationToken.None))
        {
            await second.StatAsync("/", CancellationToken.None);
        }

        var retained = Assert.Single(inner.Sessions);
        Assert.Equal(1, inner.OpenCount);
        Assert.Equal(2, retained.StatCount);
        Assert.Equal(0, retained.DisposeCount);

        sessions.Dispose();

        Assert.Equal(1, retained.DisposeCount);
    }

    [Fact]
    public async Task UnhealthySessionIsDisposedAndReplacedOnNextLease()
    {
        var inner = new RecordingSessionFactory();
        using var sessions = new RetainedRemoteFileSessionFactory(inner);

        await using (await sessions.OpenAsync(CancellationToken.None))
        {
            inner.Sessions[0].Healthy = false;
        }

        await using (await sessions.OpenAsync(CancellationToken.None))
        {
        }

        Assert.Equal(2, inner.OpenCount);
        Assert.Equal(1, inner.Sessions[0].DisposeCount);
        Assert.Equal(0, inner.Sessions[1].DisposeCount);
    }

    [Fact]
    public async Task ConcurrentLeaseWaitsWithoutOpeningAnotherSession()
    {
        var inner = new RecordingSessionFactory();
        using var sessions = new RetainedRemoteFileSessionFactory(inner);
        await using var first = await sessions.OpenAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var waiting = sessions.OpenAsync(cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, inner.OpenCount);
    }

    [Fact]
    public async Task ProviderLocalTransferUsesOneLeaseWithoutDeadlocking()
    {
        var inner = new RecordingSessionFactory();
        using var sessions = new RetainedRemoteFileSessionFactory(inner);

        await using (var pair = await sessions.OpenTransferSessionsAsync(
            CancellationToken.None))
        {
            Assert.Same(pair.Source, pair.Destination);
        }

        await using (await sessions.OpenAsync(CancellationToken.None))
        {
        }

        Assert.Equal(1, inner.OpenCount);
        Assert.Equal(0, inner.Sessions[0].DisposeCount);
    }

    private sealed class RecordingSessionFactory : IRemoteHierarchicalFileSessionFactory
    {
        public List<RecordingSession> Sessions { get; } = [];

        public int OpenCount => Sessions.Count;

        public ValueTask<IRemoteHierarchicalFileSession> OpenAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = new RecordingSession();
            Sessions.Add(session);
            return ValueTask.FromResult<IRemoteHierarchicalFileSession>(session);
        }
    }

    private sealed class RecordingSession : IRetainableRemoteFileSession
    {
        public bool Healthy { get; set; } = true;

        public bool CanReuse => Healthy && DisposeCount == 0;

        public bool StatDetectsAnyLinkInPath => true;

        public int StatCount { get; private set; }

        public int DisposeCount { get; private set; }

        public ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(
            string path,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RemoteFileEntry>>([]);

        public ValueTask<RemoteFileEntry?> StatAsync(
            string path,
            CancellationToken cancellationToken)
        {
            StatCount++;
            return ValueTask.FromResult<RemoteFileEntry?>(null);
        }

        public ValueTask<Stream> OpenReadAsync(
            string path,
            long offset,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream());

        public ValueTask<Stream> OpenCreateNewAsync(
            string path,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream());

        public ValueTask CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteDirectoryAsync(
            string path,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void Dispose() => DisposeCount++;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
