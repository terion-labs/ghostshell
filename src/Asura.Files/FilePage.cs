using System.Collections.Immutable;

namespace Asura.Files;

public sealed record FilePage
{
    public FilePage(IEnumerable<FileEntry> items, FilePageToken? continuationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = [.. items];
        ContinuationToken = continuationToken;
    }

    public ImmutableArray<FileEntry> Items { get; }

    public FilePageToken? ContinuationToken { get; }
}
