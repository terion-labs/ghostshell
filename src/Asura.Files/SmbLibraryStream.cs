namespace Asura.Files;

/// <summary>Bounded sequential stream over one SMBLibrary file handle.</summary>
internal sealed class SmbLibraryStream : Stream
{
    private readonly SmbLibrarySession _session;
    private readonly object _handle;
    private readonly bool _readable;
    private readonly bool _writable;
    private readonly int _maximumChunkSize;
    private long _position;
    private int _disposed;

    private SmbLibraryStream(
        SmbLibrarySession session,
        object handle,
        bool readable,
        bool writable,
        long position,
        uint maximumChunkSize)
    {
        _session = session;
        _handle = handle;
        _readable = readable;
        _writable = writable;
        _position = position;
        _maximumChunkSize = checked((int)Math.Min(maximumChunkSize, int.MaxValue));
        if (_maximumChunkSize <= 0)
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.IoFailure,
                "The SMB server negotiated an invalid transfer size.");
        }
    }

    public override bool CanRead => _readable && Volatile.Read(ref _disposed) == 0;

    public override bool CanSeek => false;

    public override bool CanWrite => _writable && Volatile.Read(ref _disposed) == 0;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public static SmbLibraryStream CreateReader(
        SmbLibrarySession session,
        object handle,
        long offset,
        uint maximumChunkSize) => new(
            session,
            handle,
            readable: true,
            writable: false,
            offset,
            maximumChunkSize);

    public static SmbLibraryStream CreateWriter(
        SmbLibrarySession session,
        object handle,
        uint maximumChunkSize) => new(
            session,
            handle,
            readable: false,
            writable: true,
            position: 0,
            maximumChunkSize);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_readable)
        {
            throw new NotSupportedException("This SMB stream is not readable.");
        }

        if (buffer.IsEmpty)
        {
            return 0;
        }

        var requested = Math.Min(buffer.Length, _maximumChunkSize);
        var bytes = await _session
            .ReadAsync(_handle, _position, requested, cancellationToken)
            .ConfigureAwait(false);
        if (bytes.Length > requested)
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.IoFailure,
                "The SMB server returned more bytes than requested.");
        }

        bytes.CopyTo(buffer);
        _position += bytes.Length;
        return bytes.Length;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_writable)
        {
            throw new NotSupportedException("This SMB stream is not writable.");
        }

        var remaining = buffer;
        while (!remaining.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(remaining.Length, _maximumChunkSize);
            var chunk = remaining[..count].ToArray();
            var written = await _session
                .WriteAsync(_handle, _position, chunk, cancellationToken)
                .ConfigureAwait(false);
            if (written is <= 0 || written > chunk.Length)
            {
                throw new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.IoFailure,
                    "The SMB server reported an invalid write length.");
            }

            _position += written;
            remaining = remaining[written..];
        }
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => _writable
        ? _session.FlushAsync(_handle, cancellationToken).AsTask()
        : Task.CompletedTask;

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _session.CloseAsync(_handle).ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _session.CloseSynchronously(_handle);
        }

        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use ReadAsync for an SMB stream.");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use WriteAsync for an SMB stream.");

    public override void Flush() => throw new NotSupportedException("Use FlushAsync for an SMB stream.");
}
