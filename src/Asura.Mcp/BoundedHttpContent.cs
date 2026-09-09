using System.Net;

namespace Asura.Mcp;

/// <summary>
/// Preserves streaming semantics while enforcing one aggregate byte envelope
/// for JSON and SSE response bodies.
/// </summary>
internal sealed class BoundedHttpContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly int _maximumBytes;

    public BoundedHttpContent(HttpContent inner, int maximumBytes)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        _maximumBytes = maximumBytes;
        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _inner.Headers.ContentLength ?? -1;
        return length >= 0;
    }

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context)
    {
        await SerializeToStreamAsync(
                stream,
                context,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        await using var source = await CreateBoundedStreamAsync(
                cancellationToken)
            .ConfigureAwait(false);
        await source.CopyToAsync(stream, cancellationToken)
            .ConfigureAwait(false);
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        CreateBoundedStreamAsync(CancellationToken.None);

    protected override Task<Stream> CreateContentReadStreamAsync(
        CancellationToken cancellationToken) =>
        CreateBoundedStreamAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<Stream> CreateBoundedStreamAsync(
        CancellationToken cancellationToken)
    {
        var source = await _inner.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return new MaximumReadStream(source, _maximumBytes);
    }

    private sealed class MaximumReadStream(
        Stream inner,
        int maximumBytes) : Stream
    {
        private long _remaining = maximumBytes;

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            if (_remaining == 0)
            {
                return ProbeEndOfStream();
            }

            var read = inner.Read(
                buffer,
                offset,
                (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0)
            {
                return ProbeEndOfStream();
            }

            var read = inner.Read(
                buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_remaining == 0)
            {
                var probe = new byte[1];
                var extra = await inner.ReadAsync(probe, cancellationToken)
                    .ConfigureAwait(false);
                if (extra == 0)
                {
                    return 0;
                }

                throw MessageTooLarge();
            }

            var read = await inner.ReadAsync(
                    buffer[..(int)Math.Min(buffer.Length, _remaining)],
                    cancellationToken)
                .ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

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

        private int ProbeEndOfStream()
        {
            Span<byte> probe = stackalloc byte[1];
            return inner.Read(probe) == 0
                ? 0
                : throw MessageTooLarge();
        }

        private static McpTransportFailureException MessageTooLarge() =>
            new(
                McpErrorCode.MessageTooLarge,
                "An MCP HTTP response exceeded the configured byte limit.");

        private static void ValidateBuffer(
            byte[] buffer,
            int offset,
            int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count)
            {
                throw new ArgumentException(
                    "The buffer range is outside the target array.");
            }
        }
    }
}
