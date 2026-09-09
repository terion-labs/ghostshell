namespace Asura.Files;

/// <summary>
/// Presents exactly a declared prefix of a caller-owned stream. Remote transports therefore
/// cannot consume trailing data, and an early source EOF is surfaced instead of committing a
/// silently truncated object.
/// </summary>
internal sealed class ExactLengthReadStream(
    Stream source,
    long length,
    Action<long>? onProgress = null) : Stream
{
    private long _position;

    public override bool CanRead => source.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = RemainingCount(buffer.Length);
        if (count == 0)
        {
            return 0;
        }

        var read = source.Read(buffer[..count]);
        RecordRead(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var count = RemainingCount(buffer.Length);
        if (count == 0)
        {
            return 0;
        }

        var read = await source
            .ReadAsync(buffer[..count], cancellationToken)
            .ConfigureAwait(false);
        RecordRead(read);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // The IFileProvider contract leaves caller streams open.
        base.Dispose(disposing);
    }

    private int RemainingCount(int requestedCount) =>
        (int)Math.Min(requestedCount, length - _position);

    private void RecordRead(int read)
    {
        if (read == 0)
        {
            throw new EndOfStreamException(
                $"The source ended after {_position} of {length} declared bytes.");
        }

        _position += read;
        onProgress?.Invoke(_position);
    }
}
