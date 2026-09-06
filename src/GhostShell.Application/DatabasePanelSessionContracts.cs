using GhostShell.Core;

namespace GhostShell.Application;

public sealed record DatabasePanelSessionState(
    DatabasePanelBackend Backend,
    string DriverId,
    string DisplayName,
    bool IsReady,
    string? ServerVersion = null,
    string? TlsProtocol = null,
    string? SelectedCatalog = null,
    string? SelectedSchema = null,
    RedisServerFacts? Redis = null);

public readonly record struct DatabaseObjectReference
{
    public DatabaseObjectReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw new ArgumentException(
                "A database object reference must be an opaque bounded token.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record DatabaseObjectSummary(
    DatabaseObjectReference Reference,
    string Name,
    DatabaseTableKind Kind,
    string? Catalog = null,
    string? Schema = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Schema)
        ? Name
        : $"{Schema}.{Name}";
}

public sealed record DatabaseObjectPage(
    IReadOnlyList<DatabaseObjectSummary> Objects,
    bool IsTruncated);

public sealed record DatabaseObjectSnapshot(
    DatabaseObjectSummary Object,
    IReadOnlyList<DatabaseColumnSchema> Columns,
    IReadOnlyList<DatabaseIndexSchema> Indexes,
    bool CanEdit,
    string? ReadOnlyReason,
    bool IsTruncated = false);

public sealed record DatabaseTableReadRequest(
    DatabaseObjectReference Object,
    DatabaseTableQuery Query);

public sealed record DatabaseTableSnapshot(
    DatabaseObjectSummary Object,
    DatabaseTablePage Page);

public sealed record DatabaseSchemaGraphSnapshot(
    IReadOnlyList<DatabaseSchemaTable> Tables,
    bool IsTruncated);

public readonly record struct RedisKeyReferenceId
{
    public RedisKeyReferenceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw new ArgumentException(
                "A Redis key reference must be an opaque bounded token.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record RedisKeyItem(
    RedisKeyReferenceId Reference,
    string DisplayName,
    string Type,
    TimeSpan? TimeToLive,
    long? MemoryBytes);

public sealed record RedisKeyPage(
    IReadOnlyList<RedisKeyItem> Keys,
    string? NextCursor,
    bool IsComplete);

public sealed record RedisKeyReadRequest(
    RedisKeyReferenceId Key,
    int MaximumEntries);

public sealed record RedisKeyValueSnapshot(
    RedisKeyItem Key,
    long? Length,
    IReadOnlyList<RedisValueEntry> Entries,
    bool IsTruncated,
    string? Limitation);

/// <summary>
/// One hosted Database Viewer engine. Backend-specific operations stay
/// separate so a Redis session cannot accidentally acquire a SQL surface.
/// </summary>
public interface IDatabasePanelSession : IPanelSession
{
    DatabaseSessionBinding Binding { get; }

    DatabasePanelSessionState State { get; }
}

public interface IRelationalDatabasePanelSession : IDatabasePanelSession
{
    ValueTask<DatabaseObjectPage> ListObjectsAsync(
        int maximumObjects,
        CancellationToken cancellationToken);

    ValueTask<DatabaseObjectSnapshot> DescribeObjectAsync(
        DatabaseObjectReference reference,
        CancellationToken cancellationToken);

    ValueTask<DatabaseTableSnapshot> ReadTableAsync(
        DatabaseTableReadRequest request,
        CancellationToken cancellationToken);

    ValueTask<DatabaseSchemaGraphSnapshot> ReadSchemaGraphAsync(
        int maximumObjects,
        CancellationToken cancellationToken);
}

public interface IRedisDatabasePanelSession : IDatabasePanelSession
{
    ValueTask<RedisKeyPage> ScanAsync(
        string pattern,
        string? cursor,
        int count,
        CancellationToken cancellationToken);

    ValueTask<RedisKeyValueSnapshot> ReadAsync(
        RedisKeyReadRequest request,
        CancellationToken cancellationToken);

    ValueTask<RedisSearchIndexPage> ListSearchIndexesAsync(
        int maximumIndexes,
        CancellationToken cancellationToken);

    ValueTask<RedisSearchResult> SearchAsync(
        string index,
        string query,
        int limit,
        CancellationToken cancellationToken);
}

public sealed record RedisSearchIndexPage(
    IReadOnlyList<RedisSearchIndex> Indexes,
    bool IsTruncated);

public interface IDatabasePanelSessionFactory
{
    CapabilitySet RelationalCapabilities { get; }

    CapabilitySet RedisCapabilities { get; }

    ValueTask<IDatabasePanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        DatabaseSessionTarget target,
        CancellationToken cancellationToken);
}
