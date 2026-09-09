namespace Asura.Files;

/// <summary>A ranged read that can emit at most <see cref="MaximumBytes"/> bytes.</summary>
public sealed record FileReadRequest
{
    public FileReadRequest(FileLocation location, long offset, long maximumBytes, int bufferSize)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        Location = location;
        Offset = offset;
        MaximumBytes = maximumBytes;
        BufferSize = bufferSize;
    }

    public FileLocation Location { get; }

    public long Offset { get; }

    public long MaximumBytes { get; }

    public int BufferSize { get; }
}
