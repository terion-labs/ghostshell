namespace Asura.Files;

/// <summary>Preserves streaming while translating transport exceptions at the vendor boundary.</summary>
internal sealed class ExceptionMappingStream(
    Stream inner,
    Func<Exception, Exception> mapException) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => Execute(() => inner.Length);

    public override long Position
    {
        get => Execute(() => inner.Position);
        set => Execute(() => inner.Position = value);
    }

    public override void Flush() => Execute(inner.Flush);

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => inner.FlushAsync(cancellationToken));

    public override int Read(byte[] buffer, int offset, int count) =>
        Execute(() => inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return inner.Read(buffer);
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw mapException(exception);
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw mapException(exception);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        Execute(() => inner.Seek(offset, origin));

    public override void SetLength(long value) => Execute(() => inner.SetLength(value));

    public override void Write(byte[] buffer, int offset, int count) =>
        Execute(() => inner.Write(buffer, offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            inner.Write(buffer);
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw mapException(exception);
        }
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw mapException(exception);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Execute(inner.Dispose);
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw mapException(exception);
        }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private T Execute<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw mapException(exception);
        }
    }

    private void Execute(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw mapException(exception);
        }
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception exception) when (ShouldMap(exception))
        {
            throw mapException(exception);
        }
    }

    private static bool ShouldMap(Exception exception) =>
        exception is not OperationCanceledException
        && exception is not RemoteFileSessionException;
}
