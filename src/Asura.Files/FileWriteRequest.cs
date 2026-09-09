namespace Asura.Files;

/// <summary>A streaming write with a declared, provider-checked content length.</summary>
public sealed record FileWriteRequest
{
    public FileWriteRequest(
        FileLocation location,
        long contentLength,
        int bufferSize,
        FileMutationPrecondition precondition)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        ArgumentNullException.ThrowIfNull(precondition);

        Location = location;
        ContentLength = contentLength;
        BufferSize = bufferSize;
        Precondition = precondition;
    }

    public FileLocation Location { get; }

    public long ContentLength { get; }

    public int BufferSize { get; }

    public FileMutationPrecondition Precondition { get; }
}
