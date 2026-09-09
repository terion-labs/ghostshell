using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Asura.Databases.IntegrationTests;

internal sealed record DatabaseIndexExpectations(
    bool FirstColumnDescending,
    string? IncludedColumn = null,
    string? PredicateFragment = null);

internal sealed record DatabaseProviderExpectations(
    bool CanEdit,
    bool HasIndexes,
    bool HasIdentity,
    bool HasGeneratedColumn,
    IReadOnlySet<Asura.Application.DatabaseValueKind> RequiredValueKinds,
    string? CompatibilityNote = null,
    long? ExpectedCodeLength = null,
    int? ExpectedScorePrecision = null,
    int? ExpectedScoreScale = null,
    DatabaseIndexExpectations? ScoreIndex = null);

internal sealed record DatabaseSeed(
    IReadOnlyList<string> Statements,
    string RowsTable = "viewer_rows",
    string KeylessTable = "viewer_keyless",
    string View = "viewer_rows_view",
    string HostileTable = "viewer odd.table",
    Asura.Application.DatabaseTableKind KeylessKind =
        Asura.Application.DatabaseTableKind.Table);

internal abstract record DatabaseProviderCase(
    string Id,
    string DisplayName,
    string ReadySql,
    DatabaseSeed Seed,
    DatabaseProviderExpectations Expectations)
{
    public abstract Task<DatabaseTestEnvironment> StartAsync(
        CancellationToken cancellationToken);
}

internal sealed record ContainerDatabaseProviderCase(
    string Id,
    string DisplayName,
    string Image,
    ushort Port,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Command,
    Func<string, ushort, string> ConnectionString,
    string ReadySql,
    DatabaseSeed Seed,
    DatabaseProviderExpectations Expectations,
    string? Platform = null)
    : DatabaseProviderCase(Id, DisplayName, ReadySql, Seed, Expectations)
{
    public override async Task<DatabaseTestEnvironment> StartAsync(
        CancellationToken cancellationToken)
    {
        var builder = new ContainerBuilder(Image)
            .WithPortBinding(Port, assignRandomHostPort: true)
            .WithCreateParameterModifier(BindPublishedPortsToLoopback)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(
                    Port,
                    strategy => strategy
                        .WithRetries(120)
                        .WithInterval(TimeSpan.FromSeconds(1))
                        .WithTimeout(TimeSpan.FromMinutes(2))));

        foreach (var variable in Environment)
        {
            builder = builder.WithEnvironment(variable.Key, variable.Value);
        }

        if (Command.Count > 0)
        {
            builder = builder.WithCommand([.. Command]);
        }

        if (!string.IsNullOrWhiteSpace(Platform))
        {
            builder = builder.WithCreateParameterModifier(parameters =>
                parameters.Platform = Platform);
        }

        var container = builder.Build();
        try
        {
            await container.StartAsync(cancellationToken).ConfigureAwait(false);
            var connectionString = ConnectionString(
                container.Hostname,
                container.GetMappedPublicPort(Port));
            return DatabaseTestEnvironment.ForContainer(this, connectionString, container);
        }
        catch
        {
            await container.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static void BindPublishedPortsToLoopback(CreateContainerParameters parameters)
    {
        if (parameters.HostConfig?.PortBindings is null)
        {
            return;
        }

        foreach (var bindings in parameters.HostConfig.PortBindings.Values)
        {
            foreach (var binding in bindings)
            {
                binding.HostIP = "127.0.0.1";
            }
        }
    }
}

internal sealed record FileDatabaseProviderCase(
    string Id,
    string DisplayName,
    string FileExtension,
    Func<string, string> ConnectionString,
    string ReadySql,
    DatabaseSeed Seed,
    DatabaseProviderExpectations Expectations)
    : DatabaseProviderCase(Id, DisplayName, ReadySql, Seed, Expectations)
{
    public override Task<DatabaseTestEnvironment> StartAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(
            Path.GetTempPath(),
            $"asura-db-conformance-{Guid.NewGuid():N}{FileExtension}");
        return Task.FromResult(DatabaseTestEnvironment.ForFile(
            this,
            ConnectionString(path),
            path));
    }
}

internal sealed class DatabaseTestEnvironment : IAsyncDisposable
{
    private readonly IContainer? _container;
    private readonly string? _filePath;

    private DatabaseTestEnvironment(
        DatabaseProviderCase provider,
        string connectionString,
        IContainer? container,
        string? filePath)
    {
        Provider = provider;
        ConnectionString = connectionString;
        _container = container;
        _filePath = filePath;
    }

    public DatabaseProviderCase Provider { get; }

    public string ConnectionString { get; }

    public static DatabaseTestEnvironment ForContainer(
        DatabaseProviderCase provider,
        string connectionString,
        IContainer container) =>
        new(provider, connectionString, container, filePath: null);

    public static DatabaseTestEnvironment ForFile(
        DatabaseProviderCase provider,
        string connectionString,
        string filePath) =>
        new(provider, connectionString, container: null, filePath);

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }

        if (_filePath is not null && File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }
}
