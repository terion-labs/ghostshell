namespace Asura.Files.Tests;

internal sealed class CancellingReadStream : Stream
{
    private readonly CancellationTokenSource _cancellation;
    private readonly int _length;
    private bool _returnedFirstByte;

    public CancellingReadStream(CancellationTokenSource cancellation, int length)
    {
        _cancellation = cancellation;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _returnedFirstByte ? 1 : 0;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_returnedFirstByte)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        buffer.Span[0] = 0x2A;
        _returnedFirstByte = true;
        _cancellation.Cancel();
        return ValueTask.FromResult(1);
    }

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
