using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Databases;

internal sealed partial class RelationalDatabasePanelSession : IRelationalDatabasePanelSession
{
    public const int MaximumObjects = 500;
    public const int MaximumRows = 200;
    public const int MaximumFilters = 16;
    public const int MaximumSorts = 8;
    public const int MaximumOffset = 1_000_000;

    private readonly IDatabasePanelClient _client;
    private readonly DatabasePanelSessionLifetime _lifetime;
    private readonly DatabaseOpaqueReferencePool<DatabaseTableDescriptor> _objects = new();
    private readonly DatabaseSessionTarget _target;

    public RelationalDatabasePanelSession(
        SessionId id,
        DatabaseSessionTarget target,
        string displayName,
        IDatabasePanelClient client,
        CapabilitySet capabilities,
        TimeProvider timeProvider)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Binding = target.Binding;
        State = new DatabasePanelSessionState(
            DatabasePanelBackend.Relational,
            target.DriverId,
            displayName,
            IsReady: true);
        _lifetime = new DatabasePanelSessionLifetime(
            id,
            capabilities,
            "Relational database metadata is ready.",
            timeProvider);
    }

    public SessionId Id => _lifetime.Id;

    public PanelKind Kind => PanelKind.DatabaseViewer;

    public CapabilitySet Capabilities => _lifetime.Capabilities;

    public DatabaseSessionBinding Binding { get; }

    public DatabasePanelSessionState State { get; }

    public async ValueTask<DatabaseObjectPage> ListObjectsAsync(
        int maximumObjects,
        CancellationToken cancellationToken)
    {
        RequireOpen();
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumObjects, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumObjects, MaximumObjects);
        using var operation = _lifetime.CreateOperationCancellation(cancellationToken);
        var source = await _target
            .UseConnectionStringAsync(connectionString => _client
                .ListTablesAsync(
                    _target.DriverId,
                    connectionString,
                    _target.Tunnel,
                    operation.Token))
            .ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
        var selected = source
            .OrderBy(item => item.Catalog, StringComparer.Ordinal)
            .ThenBy(item => item.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Take(maximumObjects)
            .ToArray();
        return ProjectObjectPage(
            selected,
            source.Count > selected.Length);
    }

    public async ValueTask<DatabaseObjectSnapshot> DescribeObjectAsync(
        DatabaseObjectReference reference,
        CancellationToken cancellationToken)
    {
        RequireOpen();
        var databaseObject = Resolve(reference);
        using var operation = _lifetime.CreateOperationCancellation(cancellationToken);
        var details = await _target
            .UseConnectionStringAsync(connectionString => _client
                .GetObjectDetailsAsync(
                    _target.DriverId,
                    connectionString,
                    _target.Tunnel,
                    databaseObject,
                    operation.Token))
            .ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
        if (details?.Object is not { } returnedObject
            || returnedObject.Kind != databaseObject.Kind
            || !string.Equals(returnedObject.Name, databaseObject.Name, StringComparison.Ordinal)
            || !string.Equals(returnedObject.Catalog, databaseObject.Catalog, StringComparison.Ordinal)
            || !string.Equals(returnedObject.Schema, databaseObject.Schema, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The database provider described a different object than requested.");
        }

        return ProjectObjectSnapshot(details);
    }

    public async ValueTask<DatabaseTableSnapshot> ReadTableAsync(
        DatabaseTableReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOpen();
        ValidateQuery(request.Query);
        var databaseObject = Resolve(request.Object);
        using var operation = _lifetime.CreateOperationCancellation(cancellationToken);
        var page = await _target
            .UseConnectionStringAsync(connectionString => _client
                .ReadTableAsync(
                    _target.DriverId,
                    connectionString,
                    _target.Tunnel,
                    databaseObject,
                    request.Query,
                    operation.Token))
            .ConfigureAwait(false);
        using var resultContent = page.Result;
        operation.Token.ThrowIfCancellationRequested();
        var result = new DatabaseTableSnapshot(
            ProjectObject(databaseObject),
            ProjectTablePage(page, request.Query));
        EnsureSerializedBound(result, nameof(page));
        return result;
    }

    public async ValueTask<DatabaseSchemaGraphSnapshot> ReadSchemaGraphAsync(
        int maximumObjects,
        CancellationToken cancellationToken)
    {
        RequireOpen();
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumObjects, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumObjects, MaximumObjects);
        using var operation = _lifetime.CreateOperationCancellation(cancellationToken);
        var graph = await _target
            .UseConnectionStringAsync(connectionString => _client
                .GetDatabaseSchemaGraphAsync(
                    _target.DriverId,
                    connectionString,
                    _target.Tunnel,
                    operation.Token))
            .ConfigureAwait(false);
        operation.Token.ThrowIfCancellationRequested();
        return ProjectSchemaGraph(graph, maximumObjects);
    }

    public ValueTask<PanelSessionSnapshot> SnapshotAsync(
        CancellationToken cancellationToken) =>
        _lifetime.SnapshotAsync(cancellationToken);

    public IAsyncEnumerable<PanelSessionEvent> WatchAsync(
        long afterSequence,
        CancellationToken cancellationToken) =>
        _lifetime.WatchAsync(afterSequence, cancellationToken);

    public ValueTask<PanelCloseOutcome> CloseAsync(
        PanelCloseMode mode,
        CancellationToken cancellationToken) =>
        _lifetime.CloseAsync(mode, cancellationToken);

    public ValueTask DisposeAsync() => _lifetime.DisposeAsync();

    private DatabaseObjectSummary ProjectObject(DatabaseTableDescriptor value)
    {
        value = CopyDescriptor(value, nameof(value));
        var reference = new DatabaseObjectReference(_objects.Lease(value));
        return new DatabaseObjectSummary(
            reference,
            value.Name,
            value.Kind,
            value.Catalog,
            value.Schema);
    }

    private DatabaseTableDescriptor Resolve(DatabaseObjectReference reference)
    {
        if (!_objects.TryResolve(reference.Value, out var value) || value is null)
        {
            throw new KeyNotFoundException(
                "The database object reference is unknown or expired.");
        }

        return value;
    }

    private void RequireOpen()
    {
        if (!_lifetime.IsOpen)
        {
            throw new ObjectDisposedException(nameof(RelationalDatabasePanelSession));
        }
    }

    private static void ValidateQuery(DatabaseTableQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Filters);
        ArgumentNullException.ThrowIfNull(query.Sorts);
        var includedColumns = query.Columns ?? [];
        var excludedColumns = query.ExcludeColumns ?? [];
        if (query.Offset is < 0 or > MaximumOffset
            || query.Limit is < 1 or > MaximumRows
            || query.Filters.Count > MaximumFilters
            || query.Sorts.Count > MaximumSorts
            || includedColumns.Count > 64
            || excludedColumns.Count > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                "The database table request exceeds its fixed bounds.");
        }

        if ((includedColumns.Count > 0 && excludedColumns.Count > 0)
            || includedColumns.Distinct(StringComparer.Ordinal).Count() != includedColumns.Count
            || excludedColumns.Distinct(StringComparer.Ordinal).Count() != excludedColumns.Count)
        {
            throw new ArgumentException(
                "The database table column projection is invalid.",
                nameof(query));
        }
    }
}
