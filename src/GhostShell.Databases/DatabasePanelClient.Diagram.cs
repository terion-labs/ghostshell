using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Databases;

public sealed partial class DatabasePanelClient
{
    public Task<IDatabaseDiagramSession> OpenDatabaseDiagramAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken) =>
        OpenDiagramCoreAsync(driverId, connectionString, tunnel, DatabaseDiagramPurpose.Display, cancellationToken);

    public async Task ExportDatabaseSchemaSourceAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await using var session = await OpenDiagramCoreAsync(driverId, connectionString, tunnel,
            DatabaseDiagramPurpose.SourceExport, cancellationToken).ConfigureAwait(false);
        await session.ExportAsync(destination, DatabaseDiagramExport.MermaidMarkdown, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IDatabaseDiagramSession> OpenDiagramCoreAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseDiagramPurpose purpose,
        CancellationToken cancellationToken)
    {
        var workers = _diagramWorkers ?? throw new NotSupportedException(
            "The isolated database schema renderer is unavailable.");
        if (_operationExecutor is not null)
        {
            var graph = await GetDatabaseSchemaGraphAsync(driverId, connectionString, tunnel, cancellationToken)
                .ConfigureAwait(false);
            return await workers.OpenAsync(graph, cancellationToken, purpose).ConfigureAwait(false);
        }

        var driver = Resolve(driverId);
        var normalized = driver.NormalizeConnectionString(connectionString);
        tunnel ??= driver.Descriptor.DefaultPort is null ? null : _defaultTunnel;
        if (tunnel is null)
        {
            return await workers.OpenAsync(new DatabaseWorkerConnection(driverId, normalized), cancellationToken, purpose).ConfigureAwait(false);
        }

        var endpoint = driver.GetEndpoint(normalized)
            ?? throw new InvalidOperationException("The database has no routeable endpoint.");
        if (_tunnelFactory is null)
        {
            throw new InvalidOperationException("The database route is unavailable.");
        }

        var route = await GetRouteAsync(tunnel, driver.Descriptor.Id, endpoint).ConfigureAwait(false);
        return await workers.OpenAsync(new DatabaseWorkerConnection(driverId, normalized)
        { Route = route.WorkerCapability }, cancellationToken, purpose).ConfigureAwait(false);
    }
}
