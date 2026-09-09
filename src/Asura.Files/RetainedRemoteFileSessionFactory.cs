namespace Asura.Files;

/// <summary>
/// Retains one authenticated remote session and lends it to one provider operation at a time.
/// The protocol session owns health detection; this class owns serialization and lifetime.
/// </summary>
internal sealed class RetainedRemoteFileSessionFactory(
    IRemoteHierarchicalFileSessionFactory inner) :
    IRemoteHierarchicalFileSessionFactory,
    IRemoteFileTransferSessionFactory,
    IDisposable
{
    private readonly SemaphoreSlim _leaseGate = new(1, 1);
    private IRetainableRemoteFileSession? _retained;
    private bool _disposed;

    public async ValueTask<IRemoteHierarchicalFileSession> OpenAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_retained?.CanReuse != true)
            {
                _retained?.Dispose();
                _retained = await OpenRetainableAsync(cancellationToken).ConfigureAwait(false);
            }

            var retained = _retained
                ?? throw new InvalidOperationException(
                    "The retained remote session was not created.");
            return new Lease(this, retained);
        }
        catch
        {
            _leaseGate.Release();
            throw;
        }
    }

    public async ValueTask<RemoteFileTransferSessions> OpenTransferSessionsAsync(
        CancellationToken cancellationToken)
    {
        var lease = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return new RemoteFileTransferSessions(lease, lease);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _leaseGate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _retained?.Dispose();
            _retained = null;
        }
        finally
        {
            _leaseGate.Release();
            _leaseGate.Dispose();
        }
    }

    private async ValueTask<IRetainableRemoteFileSession> OpenRetainableAsync(
        CancellationToken cancellationToken)
    {
        var session = await inner.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (session is IRetainableRemoteFileSession retainable)
        {
            return retainable;
        }

        await session.DisposeAsync().ConfigureAwait(false);
        throw new InvalidOperationException(
            "A retained remote-session factory requires a health-aware session.");
    }

    private void Return(IRetainableRemoteFileSession session)
    {
        try
        {
            if (!ReferenceEquals(session, _retained))
            {
                session.Dispose();
            }
            else if (!session.CanReuse)
            {
                session.Dispose();
                _retained = null;
            }
        }
        finally
        {
            _leaseGate.Release();
        }
    }

    private sealed class Lease(
        RetainedRemoteFileSessionFactory owner,
        IRetainableRemoteFileSession session) : IRemoteHierarchicalFileSession
    {
        private RetainedRemoteFileSessionFactory? _owner = owner;

        public bool StatDetectsAnyLinkInPath => session.StatDetectsAnyLinkInPath;

        public ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(
            string path,
            CancellationToken cancellationToken) =>
            session.ListAsync(path, cancellationToken);

        public ValueTask<RemoteFileEntry?> StatAsync(
            string path,
            CancellationToken cancellationToken) =>
            session.StatAsync(path, cancellationToken);

        public ValueTask<Stream> OpenReadAsync(
            string path,
            long offset,
            CancellationToken cancellationToken) =>
            session.OpenReadAsync(path, offset, cancellationToken);

        public ValueTask<Stream> OpenCreateNewAsync(
            string path,
            CancellationToken cancellationToken) =>
            session.OpenCreateNewAsync(path, cancellationToken);

        public ValueTask CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken) =>
            session.CreateDirectoryAsync(path, cancellationToken);

        public ValueTask RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken) =>
            session.RenameAsync(sourcePath, destinationPath, cancellationToken);

        public ValueTask DeleteFileAsync(
            string path,
            CancellationToken cancellationToken) =>
            session.DeleteFileAsync(path, cancellationToken);

        public ValueTask DeleteDirectoryAsync(
            string path,
            CancellationToken cancellationToken) =>
            session.DeleteDirectoryAsync(path, cancellationToken);

        public ValueTask<int?> GetPermissionsAsync(
            string path,
            CancellationToken cancellationToken) =>
            session.GetPermissionsAsync(path, cancellationToken);

        public ValueTask SetPermissionsAsync(
            string path,
            int mode,
            CancellationToken cancellationToken) =>
            session.SetPermissionsAsync(path, mode, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.Return(session);
            return ValueTask.CompletedTask;
        }
    }
}
