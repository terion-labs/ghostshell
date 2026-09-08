namespace GhostShell.Application;

/// <summary>
/// Complete detached cell content. Text content is UTF-8; binary content is
/// unchanged. JSON kind denotes a complete worker-validated JSON document;
/// invalid JSON remains Text. Open streams retain storage until they close.
/// </summary>
public abstract class DatabaseValueContent
{
    public abstract long Length { get; }

    public abstract DatabaseValueKind Kind { get; }

    /// <summary>Exact numeric CLR identity for a detached large scalar, when needed.</summary>
    public virtual DatabaseArrayScalarType? ScalarType => null;

    public abstract Stream OpenRead();

    public override string ToString() =>
        "Full database value; open its content stream.";
}

/// <summary>
/// Storage owned by one query result, not a global preview cache. The host
/// supplies the encrypted implementation; providers only write detached bytes.
/// Disposing prevents new reads, but existing read leases finish normally.
/// </summary>
public abstract class DatabaseValueContentStore : IDisposable
{
    /// <summary>
    /// Retains the complete result for a view/export/editor that may open more
    /// than one cell stream. Dispose the lease when that operation finishes.
    /// </summary>
    public abstract IDisposable Retain();

    public abstract Task<DatabaseValueContent> StoreAsync(
        DatabaseValueKind kind,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected abstract void Dispose(bool disposing);
}
