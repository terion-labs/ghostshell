using Asura.Application;

namespace Asura.Files;

/// <summary>Result-local ownership over the existing encrypted content engine.</summary>
public sealed class DatabaseResultContentStore(string directory) : DatabaseValueContentStore
{
    private readonly object _gate = new();
    private readonly PreviewContentCache _cache = new(directory: directory);
    private int _leases = 1;
    private int _owners = 1;
    private bool _ownerReleased;
    private bool _disposed;

    public override IDisposable Retain()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _owners++;
            _leases++;
            return new ResultLease(this);
        }
    }

    public override async Task<DatabaseValueContent> StoreAsync(
        DatabaseValueKind kind,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        Acquire();
        try
        {
            // A page can contain thousands of cells. Use the encrypted disk
            // tier even when each cell is below the file-preview threshold.
            using var pending = _cache.BeginPut(key: null, sizeHint: long.MaxValue);
            await write(pending.Destination, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new Content(this, pending.Commit(), kind);
        }
        finally
        {
            Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        lock (_gate)
        {
            if (_ownerReleased)
            {
                return;
            }

            _ownerReleased = true;
            ReleaseOwner();
        }
    }

    private void ReleaseOwner()
    {
        lock (_gate)
        {
            _disposed = --_owners == 0;
            Release();
        }
    }

    private sealed class ResultLease(DatabaseResultContentStore owner) : IDisposable
    {
        private DatabaseResultContentStore? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseOwner();
    }

    private void Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _leases++;
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_leases == 0)
            {
                _cache.Dispose();
            }
        }
    }

    private sealed class Content(DatabaseResultContentStore owner, FilePreviewContent content, DatabaseValueKind kind)
        : DatabaseValueContent
    {
        public override long Length => content.Length;

        public override DatabaseValueKind Kind => kind;

        public override Stream OpenRead()
        {
            owner.Acquire();
            try
            {
                return new ReadLease(content.OpenRead(), owner);
            }
            catch
            {
                owner.Release();
                throw;
            }
        }
    }

    private sealed class ReadLease(Stream inner, DatabaseResultContentStore owner) : Stream
    {
        private bool _disposed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                try
                {
                    inner.Dispose();
                }
                finally
                {
                    owner.Release();
                }
            }

            base.Dispose(disposing);
        }
    }
}
