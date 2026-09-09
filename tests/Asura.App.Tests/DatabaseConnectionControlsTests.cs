using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

/// <summary>
/// The panel's connection controls: the session facts and database choices
/// read after connecting, the database switcher, and disconnect. These are
/// the behaviors behind the header row and the status bar.
/// </summary>
public sealed class DatabaseConnectionControlsTests
{
    [Fact]
    public async Task Connecting_reads_the_session_facts_and_database_choices()
    {
        var client = new SessionFactsClient
        {
            SessionInfo = new DatabaseSessionInfo("16.4", "TLSv1.3"),
            DatabaseNames = ["app", "postgres", "staging"],
        };
        using var panel = CreatePanel(client, "Host=db.internal;Database=app;Username=ops");
        await panel.Initialization;

        Assert.True(panel.IsConnected);
        Assert.True(panel.HasDatabaseChoices);
        Assert.Equal(["app", "postgres", "staging"], panel.Databases);
        Assert.Equal("app", panel.SelectedDatabase);
        Assert.Equal("Reconnect", panel.ConnectButtonLabel);
        Assert.Equal(
            "PostgreSQL 16.4 : TLSv1.3 : ops : app",
            panel.ConnectionSummary);
        Assert.DoesNotContain("Host=", panel.ConnectionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_another_database_reconnects_into_it()
    {
        var client = new SessionFactsClient
        {
            DatabaseNames = ["app", "analytics"],
        };
        using var panel = CreatePanel(client, "Host=db.internal;Database=app");
        await panel.Initialization;

        panel.SelectedDatabase = "analytics";
        while (panel.IsBusy)
        {
            await Task.Yield();
        }

        Assert.True(panel.IsConnected);
        Assert.Contains(
            "Database=analytics",
            client.LastConnectionString,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Database=app",
            client.LastConnectionString,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disconnecting_forgets_the_session_but_keeps_the_target()
    {
        var client = new SessionFactsClient
        {
            SessionInfo = new DatabaseSessionInfo("16.4", "TLSv1.3"),
            DatabaseNames = ["app"],
        };
        using var panel = CreatePanel(client, "Host=db.internal;Database=app");
        await panel.Initialization;
        Assert.True(panel.IsConnected);

        panel.Disconnect();

        Assert.False(panel.IsConnected);
        Assert.Empty(panel.Tables);
        Assert.False(panel.HasDatabaseChoices);
        Assert.Equal(string.Empty, panel.ConnectionSummary);
        Assert.Equal("Connect", panel.ConnectButtonLabel);
        // The target survives, so Connect brings the session back.
        await panel.ConnectAsync();
        Assert.True(panel.IsConnected);
    }

    /// <summary>
    /// The probes decorate a proven connection; a server that refuses them
    /// still connects, with an unadorned summary.
    /// </summary>
    [Fact]
    public async Task A_refused_probe_does_not_fail_the_connection()
    {
        var client = new SessionFactsClient
        {
            ProbesFail = true,
        };
        using var panel = CreatePanel(client, "Host=db.internal;Database=app;Username=ops");
        await panel.Initialization;

        Assert.True(panel.IsConnected);
        Assert.False(panel.HasError);
        Assert.False(panel.HasDatabaseChoices);
        Assert.Equal("PostgreSQL : ops : app", panel.ConnectionSummary);
    }

    private static DatabaseRuntimePanelViewModel CreatePanel(
        SessionFactsClient client,
        string connectionString) =>
        new(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "postgres",
            connectionString: connectionString);

    /// <summary>
    /// A structural fake: real Key=Value decomposition, configurable probe
    /// answers, and a record of the string each operation connected with.
    /// </summary>
    private sealed class SessionFactsClient : IDatabasePanelClient
    {
        public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } =
        [
            new("postgres", "PostgreSQL", "Host=…", DefaultPort: 5432, CanListDatabases: true),
        ];

        public DatabaseSessionInfo SessionInfo { get; init; } = new();

        public IReadOnlyList<string> DatabaseNames { get; init; } = [];

        public bool ProbesFail { get; init; }

        public string LastConnectionString { get; private set; } = string.Empty;

        public Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken)
        {
            LastConnectionString = connectionString;
            return Task.FromResult<IReadOnlyList<DatabaseTableDescriptor>>(
                [new("people", DatabaseTableKind.Table)]);
        }

        public Task<DatabaseSessionInfo> DescribeSessionAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken) =>
            ProbesFail
                ? Task.FromException<DatabaseSessionInfo>(
                    new InvalidOperationException("permission denied"))
                : Task.FromResult(SessionInfo);

        public Task<IReadOnlyList<string>> ListDatabasesAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken) =>
            ProbesFail
                ? Task.FromException<IReadOnlyList<string>>(
                    new InvalidOperationException("permission denied"))
                : Task.FromResult(DatabaseNames);

        public Task<DatabaseQueryPage> QueryAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            string sql,
            int maxRows,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public string BuildTablePreviewQuery(string driverId, string tableName, int limit) =>
            $"SELECT * FROM {tableName} LIMIT {limit};";

        public DatabaseConnectionDetails ParseConnectionDetails(
            string driverId,
            string connectionString)
        {
            var values = connectionString
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
            return new DatabaseConnectionDetails(
                values.GetValueOrDefault("Host"),
                values.TryGetValue("Port", out var port) ? int.Parse(port, System.Globalization.CultureInfo.InvariantCulture) : null,
                values.GetValueOrDefault("Database"),
                values.GetValueOrDefault("Username"),
                values.GetValueOrDefault("Password"));
        }

        public string BuildConnectionString(string driverId, DatabaseConnectionDetails details)
        {
            var pairs = new List<string>();
            if (details.Host is { } host)
            {
                pairs.Add($"Host={host}");
            }

            if (details.Port is { } port)
            {
                pairs.Add($"Port={port}");
            }

            if (details.Database is { } database)
            {
                pairs.Add($"Database={database}");
            }

            if (details.Username is { } username)
            {
                pairs.Add($"Username={username}");
            }

            if (details.Password is { } password)
            {
                pairs.Add($"Password={password}");
            }

            return string.Join(';', pairs);
        }
    }
}
