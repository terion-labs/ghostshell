using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Databases;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace GhostShell.Databases.Tests;

public sealed class DatabaseTunnelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagram_worker_owns_the_forward_and_receives_the_original_TLS_identity(bool fail)
    {
        var tunnels = new RecordingTunnelFactory();
        var workers = new RecordingDiagramWorkers { Fail = fail };
        await using var client = new DatabasePanelClient(tunnels, diagramWorkers: workers);
        const string target = "Host=db.internal;Database=app;SSL Mode=VerifyFull";
        if (fail)
        {
            await Assert.ThrowsAsync<IOException>(() => client.OpenDatabaseDiagramAsync("postgres", target, SshProfile(), CancellationToken.None));
        }
        else
        {
            await using (var diagram = await client.OpenDatabaseDiagramAsync("postgres", target, SshProfile(), CancellationToken.None))
            {
                Assert.Equal(0, tunnels.DisposedCount);
                Assert.Equal("db.internal", new NpgsqlConnectionStringBuilder(workers.Connection!.ConnectionString).Host);
                Assert.Null(workers.Connection.LocalRoutePort);
                Assert.NotNull(workers.Connection.Route);
            }

            Assert.True(workers.Session.Disposed);
        }

        Assert.Equal(1, tunnels.DisposedCount);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("cockroach")]
    public void Postgres_relay_keeps_TLS_identity_and_strict_validation(string driverId)
    {
        using var connection = Assert.IsType<NpgsqlConnection>(Driver(driverId).CreateRoutedConnection(
            "Host=db.internal;Database=app;SSL Mode=VerifyFull", "127.0.0.1", 15432));
        var options = new System.Net.Security.SslClientAuthenticationOptions { TargetHost = "127.0.0.1" };
        connection.SslClientAuthenticationOptionsCallback!(options);

        Assert.Equal("db.internal", options.TargetHost);
        Assert.Null(options.RemoteCertificateValidationCallback);
        Assert.Equal(SslMode.VerifyFull, new NpgsqlConnectionStringBuilder(connection.ConnectionString).SslMode);
    }

    [Theory]
    [InlineData("", "db.internal")]
    [InlineData(";HostNameInCertificate=certificate.internal", "certificate.internal")]
    public void SqlServer_relay_keeps_certificate_name_and_validation(string extra, string expected)
    {
        var rewritten = Driver("sqlserver").RewriteEndpoint(
            $"Server=db.internal,1433;Encrypt=True;TrustServerCertificate=False{extra}", "127.0.0.1", 15432);
        var options = new SqlConnectionStringBuilder(rewritten);
        Assert.Equal(expected, options.HostNameInCertificate);
        Assert.False(options.TrustServerCertificate);
        Assert.Equal("127.0.0.1,15432", options.DataSource);
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    public void Mysql_full_TLS_verification_uses_an_explicit_logical_identity_validator(string driverId)
    {
        using var connection = Assert.IsType<MySqlConnector.MySqlConnection>(Driver(driverId).CreateRoutedConnection(
            "Server=db.internal;SslMode=VerifyFull", "127.0.0.1", 15432));
        Assert.NotNull(connection.RemoteCertificateValidationCallback);
        Assert.False(connection.RemoteCertificateValidationCallback(connection, null, null,
            System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Fact]
    public void SqlServer_tcp_prefix_does_not_become_part_of_the_TLS_hostname()
    {
        var options = new SqlConnectionStringBuilder(Driver("sqlserver").RewriteEndpoint(
            "Server=tcp:db.internal,1433;Encrypt=True", "127.0.0.1", 15432));
        Assert.Equal("db.internal", options.HostNameInCertificate);
    }

    [Theory]
    [InlineData("mysql", "Server=db.internal;ServerRedirectionMode=Required")]
    [InlineData("sqlserver", "Server=db.internal;Database=app;Failover Partner=other.internal")]
    public void Unsupported_automatic_destination_changes_cannot_bypass_the_relay(string driverId, string value)
    {
        Assert.Throws<NotSupportedException>(() => Driver(driverId).CreateRoutedConnection(value, "127.0.0.1", 15432));
    }

    [Theory]
    [InlineData("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCPS)(HOST=db.internal)(PORT=1522)))")]
    [InlineData("Data Source=tcps://db.internal:1522/app")]
    public async Task Unsupported_Oracle_address_cannot_bypass_the_workspace_route(string value)
    {
        var factory = new RecordingTunnelFactory();
        await using var client = new DatabasePanelClient(BuiltInDatabaseDrivers.All, factory, SshProfile());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListTablesAsync(
            "oracle", value, null, CancellationToken.None));
        Assert.Equal(0, factory.OpenCount);
    }

    [Theory]
    [InlineData(
        "postgres",
        "Host=db.internal;Port=5433;Database=app",
        "db.internal",
        5433)]
    [InlineData(
        "cockroach",
        "Host=db.internal;Database=app",
        "db.internal",
        5432)]
    [InlineData(
        "mysql",
        "Server=db.internal;Port=3307;Database=app",
        "db.internal",
        3307)]
    [InlineData(
        "sqlserver",
        "Server=db.internal,14330;Database=app",
        "db.internal",
        14330)]
    [InlineData(
        "sqlserver",
        "Server=db.internal;Database=app",
        "db.internal",
        1433)]
    [InlineData(
        "oracle",
        "Data Source=db.internal:1522/FREEPDB1;User Id=app",
        "db.internal",
        1522)]
    [InlineData(
        "oracle",
        "Data Source=db.internal/FREEPDB1;User Id=app",
        "db.internal",
        1521)]
    [InlineData(
        "firebird",
        "DataSource=db.internal;Port=3051;Database=/srv/app.fdb",
        "db.internal",
        3051)]
    [InlineData(
        "clickhouse",
        "Host=db.internal;Port=9004;Database=default",
        "db.internal",
        9004)]
    public void Network_drivers_expose_their_endpoint(
        string driverId,
        string connectionString,
        string expectedHost,
        int expectedPort)
    {
        var driver = Driver(driverId);

        var endpoint = driver.GetEndpoint(connectionString);

        Assert.NotNull(endpoint);
        Assert.Equal(expectedHost, endpoint.Host);
        Assert.Equal(expectedPort, endpoint.Port);

        // The rewritten string points at the forward and keeps everything else.
        var rewritten = driver.RewriteEndpoint(connectionString, "127.0.0.1", 15432);
        var forwarded = driver.GetEndpoint(rewritten);
        Assert.NotNull(forwarded);
        Assert.Equal("127.0.0.1", forwarded.Host);
        Assert.Equal(15432, forwarded.Port);
    }

    [Fact]
    public void Oracle_rewrite_keeps_the_service_name()
    {
        var driver = Driver("oracle");

        var rewritten = driver.RewriteEndpoint(
            "Data Source=db.internal:1522/FREEPDB1;User Id=app",
            "127.0.0.1",
            15210);

        Assert.Contains("127.0.0.1:15210/FREEPDB1", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void File_engines_have_no_endpoint_and_refuse_tunneling()
    {
        Assert.Null(Driver("sqlite").GetEndpoint("Data Source=/tmp/x.db"));
        Assert.Null(Driver("duckdb").GetEndpoint("Data Source=/tmp/x.duckdb"));
        Assert.Throws<InvalidOperationException>(() =>
            Driver("sqlite").RewriteEndpoint("Data Source=/tmp/x.db", "127.0.0.1", 1));
    }

    [Fact]
    public async Task Tunnel_request_for_a_file_engine_fails_before_connecting()
    {
        var factory = new RecordingTunnelFactory();
        var client = new DatabasePanelClient(BuiltInDatabaseDrivers.All, factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ListTablesAsync(
                "sqlite",
                "Data Source=/tmp/x.db",
                SshProfile(),
                CancellationToken.None));

        Assert.Contains("cannot be tunneled", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task Tunneled_query_rewrites_the_endpoint_and_reuses_the_forward()
    {
        // SQLite listens nowhere, so the "forwarded" endpoint is validated by a
        // driver stub that records what the client asked it to connect to.
        var driver = new RecordingDriver();
        var factory = new RecordingTunnelFactory();
        var client = new DatabasePanelClient([driver], factory);
        var tunnel = SshProfile();

        _ = await client.ListTablesAsync("recording", "Host=db.internal;Port=9;", tunnel, CancellationToken.None);
        _ = await client.ListTablesAsync("recording", "Host=db.internal;Port=9;", tunnel, CancellationToken.None);

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(("db.internal", 9), factory.LastTarget);
        Assert.Equal("Host=127.0.0.1;Port=45001;", driver.LastConnectionString);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderDisposedDuringConnectIsCancellationOnlyWhenRouteWasCanceled(bool canceled)
    {
        using var lifetime = new CancellationTokenSource();
        var driver = new RecordingDriver
        {
            BeforeCreate = () =>
            {
                if (canceled) { lifetime.Cancel(); }
                throw new ObjectDisposedException("synthetic-provider");
            },
        };
        var factory = new RecordingTunnelFactory { RouteLifetime = lifetime.Token };
        await using var client = new DatabasePanelClient([driver], factory);
        var operation = client.ListTablesAsync("recording", "Host=db.internal;Port=9;", SshProfile(), CancellationToken.None);
        if (canceled) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); }
        else { await Assert.ThrowsAsync<ObjectDisposedException>(() => operation); }
    }

    private static IDatabaseDriver Driver(string id) =>
        BuiltInDatabaseDrivers.All.Single(driver => string.Equals(driver.Descriptor.Id, id, StringComparison.Ordinal));

    private static ConnectionProfile SshProfile() => new(
        new ConnectionId("bastion"),
        ConnectionProfile.CurrentSchemaVersion,
        "bastion",
        new ConnectionEndpoint.Ssh("bastion.example.test", username: "ops"),
        new ConnectionAuthentication.None(),
        ConnectionStartup.Default,
        ConnectionKeepAlive.Disabled,
        SshHostKeyPolicy.AcceptNew);

    private sealed class RecordingTunnelFactory : IDatabaseTunnelFactory
    {
        public CancellationToken RouteLifetime { get; init; }
        public int OpenCount { get; private set; }
        public int DisposedCount { get; private set; }

        public (string Host, int Port)? LastTarget { get; private set; }

        public ValueTask<IDatabaseTunnelLease> OpenAsync(
            ConnectionProfile connection,
            string targetHost,
            int targetPort,
            CancellationToken cancellationToken)
        {
            OpenCount++;
            LastTarget = (targetHost, targetPort);
            return ValueTask.FromResult<IDatabaseTunnelLease>(new Lease(() => DisposedCount++));
        }

        private sealed class Lease(Action onDispose) : IDatabaseTunnelLease
        {
            public int LocalPort => 45001;

            public ValueTask DisposeAsync()
            {
                onDispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingDiagramWorkers : IDatabaseDiagramWorkerFactory
    {
        public Task<IDatabaseDiagramSession> OpenAsync(DatabaseSchemaGraph graph, CancellationToken cancellationToken,
            DatabaseDiagramPurpose purpose = DatabaseDiagramPurpose.Display) => throw new NotSupportedException();

        public bool Fail { get; init; }
        public DatabaseWorkerConnection? Connection { get; private set; }
        public DiagramSession Session { get; } = new();
        public async Task<IDatabaseDiagramSession> OpenAsync(DatabaseWorkerConnection connection, CancellationToken cancellationToken,
            DatabaseDiagramPurpose purpose = DatabaseDiagramPurpose.Display)
        {
            Connection = connection;
            var lease = await connection.Route!.OpenAsync("db.internal", 5432, cancellationToken);
            if (Fail)
            {
                await lease.DisposeAsync();
                throw new IOException("test worker failure");
            }
            Session.Route = lease;
            return Session;
        }
    }

    private sealed class DiagramSession : IDatabaseDiagramSession
    {
        public IDatabaseTunnelLease? Route { get; set; }
        public bool Disposed { get; private set; }
        public Task<byte[]> RenderViewportAsync(DatabaseDiagramViewport viewport, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<byte>());
        public Task ExportAsync(Stream destination, DatabaseDiagramExport format, CancellationToken cancellationToken) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            Disposed = true;
            if (Route is { } route) { await route.DisposeAsync(); }
        }
    }

    private sealed class RecordingDriver : IDatabaseDriver
    {
        public Action? BeforeCreate { get; init; }
        public string? LastConnectionString { get; private set; }

        public DatabaseDriverDescriptor Descriptor { get; } = new(
            "recording",
            "Recording",
            "Host=…");

        public System.Data.Common.DbConnection CreateConnection(string connectionString)
        {
            BeforeCreate?.Invoke();
            LastConnectionString = connectionString;
            // In-memory SQLite lets the client run its full pipeline without a
            // server; only the recorded connection string matters here.
            return new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        }

        public string ListTablesSql =>
            "SELECT name, type FROM sqlite_master WHERE type IN ('table','view');";

        public string QuoteIdentifier(string identifier) => identifier;

        public string BuildPreviewQuery(string tableName, int limit) =>
            $"SELECT * FROM {tableName} LIMIT {limit};";

        public DatabaseEndpoint? GetEndpoint(string connectionString)
        {
            var builder = new System.Data.Common.DbConnectionStringBuilder
            {
                ConnectionString = connectionString,
            };
            return builder.TryGetValue("Host", out var host)
                ? new DatabaseEndpoint(
                    (string)host,
                    builder.TryGetValue("Port", out var port)
                        ? int.Parse((string)port, System.Globalization.CultureInfo.InvariantCulture)
                        : 0)
                : null;
        }

        public string RewriteEndpoint(string connectionString, string host, int port) =>
            $"Host={host};Port={port};";

        public DatabaseConnectionDetails ParseDetails(string connectionString) =>
            new(Options: connectionString);

        public string BuildConnectionString(DatabaseConnectionDetails details) =>
            details.Options ?? string.Empty;
    }
}
