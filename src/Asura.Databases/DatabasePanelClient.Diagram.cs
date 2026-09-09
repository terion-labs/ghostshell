using Asura.Application;
using Asura.Core;

namespace Asura.Databases;

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
        if (ExecutesInWorker)
        {
            var graph = await GetDatabaseSchemaGraphAsync(driverId, connectionString, tunnel, cancellationToken)
                .ConfigureAwait(false);
            return await workers.OpenAsync(graph, cancellationToken, purpose).ConfigureAwait(false);
        }

        var driver = Resolve(driverId);
        RejectUnselectedSshRoute(tunnel ?? _defaultTunnel);
        return await workers.OpenAsync(new DatabaseWorkerConnection(driverId, driver.NormalizeConnectionString(connectionString)),
            cancellationToken, purpose).ConfigureAwait(false);
    }
}
