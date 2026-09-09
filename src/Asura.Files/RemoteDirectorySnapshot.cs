using System.Text;

namespace Asura.Files;

/// <summary>
/// Captures a bounded directory snapshot for remote protocols that do not provide stable
/// server-side paging. The aggregate UTF-8 name budget bounds both retained strings and sort work.
/// </summary>
internal sealed class RemoteDirectorySnapshot
{
    internal const int MaximumEntryCount = 100_000;
    internal const long MaximumNameUtf8Bytes = 8L * 1024 * 1024;

    private readonly List<RemoteFileEntry> _entries = [];
    private int _observedEntryCount;
    private long _nameUtf8Bytes;

    public void Add(RemoteFileEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        if (_observedEntryCount == MaximumEntryCount)
        {
            throw LimitExceeded();
        }

        var nameUtf8Bytes = Encoding.UTF8.GetByteCount(entry.Name);
        if (nameUtf8Bytes > MaximumNameUtf8Bytes - _nameUtf8Bytes)
        {
            throw LimitExceeded();
        }

        _observedEntryCount++;
        _nameUtf8Bytes += nameUtf8Bytes;
        if (entry.Name is not ("." or ".."))
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<RemoteFileEntry> Complete(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return [.. _entries];
    }

    public static IReadOnlyList<RemoteFileEntry> Capture(
        IReadOnlyList<RemoteFileEntry> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var snapshot = new RemoteDirectorySnapshot();
        foreach (var entry in entries)
        {
            snapshot.Add(entry, cancellationToken);
        }

        return snapshot.Complete(cancellationToken);
    }

    private static RemoteFileSessionException LimitExceeded() =>
        new(
            RemoteFileSessionErrorCode.LimitExceeded,
            $"The remote directory exceeds the bounded snapshot limit of "
            + $"{MaximumEntryCount} entries or {MaximumNameUtf8Bytes} UTF-8 name bytes.");
}
