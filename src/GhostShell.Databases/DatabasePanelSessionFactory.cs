using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Databases;

/// <summary>
/// Creates read-bounded relational or Redis engines behind the same hosted
/// Database Viewer identity. It consumes connection material but never places
/// that material in session state or errors.
/// </summary>
public sealed class DatabasePanelSessionFactory : IDatabasePanelSessionFactory
{
    private readonly IDatabasePanelClient _relational;
    private readonly IRedisPanelSessionFactory? _redis;
    private readonly TimeProvider _timeProvider;

    public DatabasePanelSessionFactory(
        IDatabasePanelClient relational,
        TimeProvider timeProvider,
        IRedisPanelSessionFactory? redis = null)
    {
        _relational = relational ?? throw new ArgumentNullException(nameof(relational));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _redis = redis;
    }

    public CapabilitySet RelationalCapabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.DatabaseReadState,
        SessionCapabilities.DatabaseListObjects,
        SessionCapabilities.DatabaseDescribeObject,
        SessionCapabilities.DatabaseReadTable,
        SessionCapabilities.DatabaseSchemaGraph,
    ]);

    public CapabilitySet RedisCapabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.DatabaseReadState,
        SessionCapabilities.RedisScan,
        SessionCapabilities.RedisRead,
        SessionCapabilities.RedisListIndexes,
        SessionCapabilities.RedisSearch,
    ]);

    public ValueTask<IDatabasePanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        DatabaseSessionTarget target,
        CancellationToken cancellationToken) =>
        CreateAsync(sessionId, target, cancellationToken);

    public async ValueTask<IDatabasePanelSession> CreateAsync(
        SessionId sessionId,
        DatabaseSessionTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Binding.Backend == DatabasePanelBackend.Redis)
        {
            if (_redis is null)
            {
                throw new NotSupportedException(
                    "Redis database sessions are unavailable in this build.");
            }

            IRedisPanelSession? redis = null;
            try
            {
                redis = await target
                    .UseConnectionStringAsync(connectionString => _redis
                        .OpenAsync(
                            connectionString,
                            target.Tunnel,
                            cancellationToken))
                    .ConfigureAwait(false);
                return new RedisDatabasePanelSession(
                    sessionId,
                    target.Binding,
                    redis,
                    RedisCapabilities,
                    _timeProvider);
            }
            catch
            {
                if (redis is not null)
                {
                    await redis.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
        }

        var driver = _relational.Drivers.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, target.DriverId, StringComparison.Ordinal)) ?? throw new NotSupportedException(
                "The requested database driver is unavailable.");

        // Connectivity is proven before the engine is admitted to SessionHost.
        // The result is discarded and refreshed through the bounded tool path.
        _ = await target
            .UseConnectionStringAsync(connectionString => _relational
                .ListTablesAsync(
                    target.DriverId,
                    connectionString,
                    target.Tunnel,
                    cancellationToken))
            .ConfigureAwait(false);
        return new RelationalDatabasePanelSession(
            sessionId,
            target,
            driver.DisplayName,
            _relational,
            RelationalCapabilities,
            _timeProvider);
    }
}
