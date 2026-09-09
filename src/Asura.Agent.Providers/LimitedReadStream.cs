using Asura.Application;

namespace Asura.Agent.Providers;

internal sealed class LimitedReadStream(Stream inner, long maximumBytes) : Stream
{
    private long _bytesRead;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, LimitCount(count));
        RecordRead(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer[..LimitCount(buffer.Length)]);
        RecordRead(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner
            .ReadAsync(buffer[..LimitCount(buffer.Length)], cancellationToken)
            .ConfigureAwait(false);
        RecordRead(read);
        return read;
    }

    public override int ReadByte()
    {
        var value = inner.ReadByte();
        if (value >= 0)
        {
            RecordRead(1);
        }

        return value;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private int LimitCount(int requested)
    {
        if (requested == 0)
        {
            return 0;
        }

        var remaining = maximumBytes - _bytesRead;
        return remaining <= 0
            ? 1
            : (int)Math.Min(requested, Math.Min(remaining + 1, int.MaxValue));
    }

    private void RecordRead(int count)
    {
        _bytesRead = checked(_bytesRead + count);
        if (_bytesRead > maximumBytes)
        {
            ThrowTooLarge();
        }
    }

    private static void ThrowTooLarge() =>
        throw AiProviderClientException.Create(AiProviderRuntimeErrorCode.ResponseTooLarge);
}
