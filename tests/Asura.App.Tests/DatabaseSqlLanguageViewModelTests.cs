using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class DatabaseSqlLanguageViewModelTests
{
    [Fact]
    public async Task Connected_panel_loads_a_detached_catalog_and_disposes_its_language_session()
    {
        var client = new CatalogDatabaseClient();
        var language = new RecordingSqlLanguageService();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db",
            sqlLanguageService: language);

        await panel.Initialization;
        await panel.SqlLanguageInitialization;

        Assert.Equal(1, client.CatalogCallCount);
        var catalog = Assert.IsType<SqlCatalogSnapshot>(language.LastCatalog);
        Assert.Equal("sqlite", catalog.DriverId);
        Assert.Equal("main", catalog.DefaultSchema);
        Assert.Same(language.Session, panel.SqlLanguageSession);
        Assert.True(panel.HasSqlLanguageSession);
        Assert.Contains("2 database objects", panel.SqlLanguageStatus, StringComparison.Ordinal);
        Assert.Contains("1 server routine", panel.SqlLanguageStatus, StringComparison.Ordinal);
        Assert.Contains("user-defined routines only", panel.SqlLanguageStatus, StringComparison.Ordinal);
        Assert.Contains("unverified built-ins are omitted", panel.SqlLanguageStatus, StringComparison.Ordinal);

        panel.Disconnect();

        Assert.Null(panel.SqlLanguageSession);
        await language.Session.Disposed;
    }

    [Fact]
    public async Task Selected_object_drives_completion_context_without_replacing_the_language_session()
    {
        var catalogGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CatalogDatabaseClient(catalogGate);
        var language = new RecordingSqlLanguageService();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db",
            initialObject: new DatabaseObjectId(null, "main", "people"),
            sqlLanguageService: language);

        await panel.Initialization;
        await client.CatalogStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(
            new DatabaseObjectId(null, "main", "people"),
            panel.SqlLanguageCompletionContext.PreferredObject);
        Assert.Null(panel.SqlLanguageSession);

        catalogGate.SetResult();
        await panel.SqlLanguageInitialization;
        var session = Assert.IsType<RecordingSqlLanguageSession>(
            panel.SqlLanguageSession);

        var replacement = Assert.Single(
            panel.Tables,
            table => string.Equals(table.Descriptor.Name, "named_people", StringComparison.Ordinal));
        await panel.PreviewTableAsync(replacement);

        Assert.Equal(replacement.Descriptor.Id, panel.SelectedObject?.Descriptor.Id);
        Assert.Equal(
            replacement.Descriptor.Id,
            panel.SqlLanguageCompletionContext.PreferredObject);
        Assert.Same(session, panel.SqlLanguageSession);

        panel.ShowDatabaseOverview();

        Assert.Null(panel.SelectedObject);
        Assert.Null(panel.SqlLanguageCompletionContext.PreferredObject);
        Assert.Same(session, panel.SqlLanguageSession);
    }

    [Fact]
    public async Task Missing_native_worker_never_reads_the_expensive_language_catalog()
    {
        var client = new CatalogDatabaseClient();
        var language = new RecordingSqlLanguageService { IsAvailable = false };
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db",
            sqlLanguageService: language);

        await panel.Initialization;
        await panel.SqlLanguageInitialization;

        Assert.Equal(0, client.CatalogCallCount);
        Assert.Null(panel.SqlLanguageSession);
        Assert.Contains("not installed", panel.SqlLanguageStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Limited_catalog_is_explicit_in_the_editor_status()
    {
        var client = new CatalogDatabaseClient { ReturnPartialCatalog = true };
        var language = new RecordingSqlLanguageService();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db",
            sqlLanguageService: language);

        await panel.Initialization;
        await panel.SqlLanguageInitialization;

        Assert.Contains("limited catalog", panel.SqlLanguageStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("safe metadata size", panel.SqlLanguageStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unavailable_routine_metadata_is_explicit_in_the_editor_status()
    {
        var client = new CatalogDatabaseClient
        {
            ReturnPartialCatalog = true,
            PartialCatalogLimitation =
                "Routine metadata was unavailable for this connection.",
        };
        var language = new RecordingSqlLanguageService();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db",
            sqlLanguageService: language);

        await panel.Initialization;
        await panel.SqlLanguageInitialization;

        Assert.Contains("1 server routine", panel.SqlLanguageStatus, StringComparison.Ordinal);
        Assert.Contains("limited catalog", panel.SqlLanguageStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Routine metadata was unavailable", panel.SqlLanguageStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SqlCatalogCoverage.None, SqlCatalogCoverage.None, "Server function metadata is unavailable")]
    [InlineData(SqlCatalogCoverage.Complete, SqlCatalogCoverage.Partial, "Server function metadata is incomplete")]
    [InlineData(SqlCatalogCoverage.Complete, SqlCatalogCoverage.None, "Intrinsic-operator metadata is unavailable")]
    public async Task Coverage_gaps_are_explicit_in_the_editor_status(
        SqlCatalogCoverage routineCoverage,
        SqlCatalogCoverage intrinsicCoverage,
        string expectedStatus)
    {
        var client = new CatalogDatabaseClient
        {
            ReturnRoutineCoverage = routineCoverage,
            ReturnIntrinsicCoverage = intrinsicCoverage,
        };
        var language = new RecordingSqlLanguageService();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db",
            sqlLanguageService: language);

        await panel.Initialization;
        await panel.SqlLanguageInitialization;

        Assert.Contains(expectedStatus, panel.SqlLanguageStatus, StringComparison.Ordinal);
        Assert.Contains("unverified", panel.SqlLanguageStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Superseded_catalog_failure_cannot_overwrite_disconnected_status()
    {
        var catalogGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new CatalogDatabaseClient(
            catalogGate,
            new InvalidOperationException("stale catalog failure"));
        var language = new RecordingSqlLanguageService();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db",
            sqlLanguageService: language);

        await panel.Initialization;
        await client.CatalogStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        panel.Disconnect();
        catalogGate.SetResult();
        await panel.SqlLanguageInitialization;

        Assert.Null(panel.SqlLanguageSession);
        Assert.DoesNotContain("stale catalog failure", panel.SqlLanguageStatus, StringComparison.Ordinal);
        Assert.Contains("Connect a database", panel.SqlLanguageStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dml_preserves_language_session_while_ddl_refreshes_the_catalog()
    {
        var client = new CatalogDatabaseClient();
        var language = new RecordingSqlLanguageService();
        using var panel = new DatabaseRuntimePanelViewModel(
            PanelInstanceId.New(),
            "Database",
            client,
            driverId: "sqlite",
            connectionString: "Data Source=demo.db",
            sqlLanguageService: language);
        await panel.Initialization;
        await panel.SqlLanguageInitialization;
        var initialSession = Assert.IsType<RecordingSqlLanguageSession>(
            panel.SqlLanguageSession);

        panel.QueryText = "UPDATE people SET id = id;";
        await panel.RunQueryAsync();

        Assert.Equal(1, client.CatalogCallCount);
        Assert.Same(initialSession, panel.SqlLanguageSession);

        panel.QueryText = "SELECT 'CREATE is data'; CREATE TABLE extra(id integer);";
        await panel.RunQueryAsync();
        await panel.SqlLanguageInitialization;

        Assert.Equal(2, client.CatalogCallCount);
        Assert.NotSame(initialSession, panel.SqlLanguageSession);
        await initialSession.Disposed;
    }

    private sealed class CatalogDatabaseClient : IDatabasePanelClient
    {
        private readonly TaskCompletionSource? _catalogGate;
        private readonly Exception? _catalogError;

        public CatalogDatabaseClient(
            TaskCompletionSource? catalogGate = null,
            Exception? catalogError = null)
        {
            _catalogGate = catalogGate;
            _catalogError = catalogError;
        }

        public int CatalogCallCount { get; private set; }

        public bool ReturnPartialCatalog { get; init; }

        public string? PartialCatalogLimitation { get; init; }

        public SqlCatalogCoverage ReturnRoutineCoverage { get; init; } =
            SqlCatalogCoverage.UserDefinedOnly;

        public SqlCatalogCoverage ReturnIntrinsicCoverage { get; init; } =
            SqlCatalogCoverage.None;

        public TaskCompletionSource CatalogStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; } =
        [
            new("sqlite", "SQLite", "Data Source=…", IsFileBased: true),
        ];

        public Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DatabaseTableDescriptor>>(
            [
                new("people", DatabaseTableKind.Table, Schema: "main"),
                new("named_people", DatabaseTableKind.View, Schema: "main"),
            ]);

        public Task<DatabaseQueryPage> QueryAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            string sql,
            int maxRows,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DatabaseQueryPage([], [], false, 0, TimeSpan.Zero));

        public string BuildTablePreviewQuery(
            string driverId,
            string tableName,
            int limit) => $"SELECT * FROM \"{tableName}\" LIMIT {limit};";

        public DatabaseConnectionDetails ParseConnectionDetails(
            string driverId,
            string connectionString) => new(FilePath: connectionString);

        public string BuildConnectionString(
            string driverId,
            DatabaseConnectionDetails details) => details.FilePath ?? string.Empty;

        public async Task<SqlCatalogSnapshot> GetSqlCatalogAsync(
            string driverId,
            string connectionString,
            ConnectionProfile? tunnel,
            CancellationToken cancellationToken)
        {
            CatalogCallCount++;
            CatalogStarted.TrySetResult();
            if (_catalogGate is not null)
            {
                await _catalogGate.Task;
            }

            if (_catalogError is not null)
            {
                throw _catalogError;
            }

            var columns = new[]
            {
                new SqlCatalogColumn(
                    "id",
                    "INTEGER",
                    DatabaseValueKind.SignedInteger,
                    IsNullable: false),
            };
            return new SqlCatalogSnapshot(
                driverId,
                DefaultCatalog: null,
                DefaultSchema: "main",
                [
                    new SqlCatalogObject(
                        new DatabaseObjectId(null, "main", "people"),
                        DatabaseTableKind.Table,
                        columns),
                    new SqlCatalogObject(
                        new DatabaseObjectId(null, "main", "named_people"),
                        DatabaseTableKind.View,
                        columns),
                ],
                ReturnPartialCatalog,
                ReturnPartialCatalog
                    ? PartialCatalogLimitation
                        ?? "The catalog reached its safe metadata size limit."
                    : null)
            {
                Routines =
                [
                    new SqlCatalogRoutine(
                        new DatabaseObjectId(null, null, "abs"),
                        SqlCatalogRoutineKind.Scalar,
                        "abs(number)",
                        [
                            new SqlCatalogRoutineParameter(
                                "value",
                                "number",
                                DatabaseValueKind.Decimal,
                                SqlCatalogRoutineParameterMode.In),
                        ],
                        "number",
                        DatabaseValueKind.Decimal,
                        MinimumArgumentCount: 1,
                        MaximumArgumentCount: 1),
                ],
                RoutineCoverage = ReturnRoutineCoverage,
                IntrinsicCoverage = ReturnIntrinsicCoverage,
            };
        }
    }

    private sealed class RecordingSqlLanguageService : ISqlLanguageService
    {
        public bool IsAvailable { get; set; } = true;

        public SqlCatalogSnapshot? LastCatalog { get; private set; }

        public RecordingSqlLanguageSession Session { get; private set; } = new();

        private int OpenCount { get; set; }

        public Task<ISqlLanguageSession> OpenSessionAsync(
            SqlCatalogSnapshot catalog,
            CancellationToken cancellationToken)
        {
            LastCatalog = catalog;
            if (OpenCount++ > 0)
            {
                Session = new RecordingSqlLanguageSession();
            }

            return Task.FromResult<ISqlLanguageSession>(Session);
        }
    }

    private sealed class RecordingSqlLanguageSession : ISqlLanguageSession
    {
        private readonly TaskCompletionSource _disposed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;

        public Task Disposed => _disposed.Task;

        public SqlCompletionContext? LastCompletionContext { get; private set; }

        public Task<SqlCompletionResult> CompleteAsync(
            string sql,
            int cursorOffset,
            CancellationToken cancellationToken) =>
            CompleteAsync(
                sql,
                cursorOffset,
                SqlCompletionContext.Empty,
                cancellationToken);

        public Task<SqlCompletionResult> CompleteAsync(
            string sql,
            int cursorOffset,
            SqlCompletionContext context,
            CancellationToken cancellationToken)
        {
            LastCompletionContext = context;
            return Task.FromResult(SqlCompletionResult.Empty);
        }

        public Task<IReadOnlyList<SqlDiagnostic>> DiagnoseAsync(
            string sql,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);

        public Task UpdateCatalogAsync(
            SqlCatalogSnapshot catalog,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
