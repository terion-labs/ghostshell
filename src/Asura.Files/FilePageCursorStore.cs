using System.Collections.Concurrent;

namespace Asura.Files;

/// <summary>
/// Keeps provider continuation state behind a bounded, transport-independent token. Remote
/// continuation values can exceed <see cref="FilePageToken"/> limits or contain control data.
/// </summary>
internal sealed class FilePageCursorStore<T>(int maximumEntries = 1_024)
    where T : class
{
    private readonly ConcurrentDictionary<string, T> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();

    public FilePageToken Add(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var key = Guid.NewGuid().ToString("N");
        _entries[key] = value;
        _insertionOrder.Enqueue(key);
        Trim();
        return new FilePageToken(key);
    }

    public bool TryGet(FilePageToken token, out T? value) =>
        _entries.TryGetValue(token.Value, out value);

    private void Trim()
    {
        while (_entries.Count > maximumEntries && _insertionOrder.TryDequeue(out var oldest))
        {
            _entries.TryRemove(oldest, out _);
        }
    }
}
