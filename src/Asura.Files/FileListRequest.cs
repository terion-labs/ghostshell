namespace Asura.Files;

public sealed record FileListRequest
{
    public FileListRequest(FileLocation location, int pageSize, FilePageToken? continuationToken = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        Location = location;
        PageSize = pageSize;
        ContinuationToken = continuationToken;
    }

    public FileLocation Location { get; }

    public int PageSize { get; }

    public FilePageToken? ContinuationToken { get; }
}
