namespace Asura.Files;

public sealed record FileProviderLimits
{
    public FileProviderLimits(
        int maximumListPageSize,
        long maximumReadBytes,
        int maximumBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumListPageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBufferSize);

        MaximumListPageSize = maximumListPageSize;
        MaximumReadBytes = maximumReadBytes;
        MaximumBufferSize = maximumBufferSize;
    }

    public int MaximumListPageSize { get; }

    public long MaximumReadBytes { get; }

    public int MaximumBufferSize { get; }
}
