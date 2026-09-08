using System.Diagnostics;
using System.Reflection;
using GhostShell.Application;
using GhostShell.DatabaseBackend;
using GhostShell.Desktop;
using GhostShell.Files;
using GhostShell.Infrastructure;
using Microsoft.Data.Sqlite;

namespace GhostShell.Architecture.Tests;

public sealed class DatabaseMetadataWorkerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllMetadataOperationsRoundTripThroughOwnedProcess(bool privateWorkspace)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        using var worker = fixture.Worker(privateWorkspace);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;

        var tables = await worker.ListTablesAsync(fixture.Connection, token);
        var children = Assert.Single(tables, table => string.Equals(table.Name, "children", StringComparison.Ordinal));
        Assert.Contains(tables, table => string.Equals(table.Name, "child_names", StringComparison.Ordinal) && table.Kind == DatabaseTableKind.View);

        var graph = await worker.GetDatabaseSchemaGraphAsync(fixture.Connection, token);
        var childTable = Assert.Single(graph.Tables, table => string.Equals(table.Object.Name, "children", StringComparison.Ordinal));
        Assert.Contains(childTable.Columns, column => string.Equals(column.Name, "name", StringComparison.Ordinal));
        Assert.Equal("parents", Assert.Single(childTable.ForeignKeys).ReferencedObject.Name);

        var catalog = await worker.GetSqlCatalogAsync(fixture.Connection, token);
        Assert.Equal("sqlite", catalog.DriverId);
        Assert.Contains(catalog.Objects, item => string.Equals(item.Id.Name, "children", StringComparison.Ordinal) && item.Columns.Any(column => string.Equals(column.Name, "name", StringComparison.Ordinal)));

        Assert.Empty(await worker.ListDatabasesAsync(fixture.Connection, token));
        var session = await worker.DescribeSessionAsync(fixture.Connection, token);
        Assert.False(string.IsNullOrWhiteSpace(session.ServerVersion));
        Assert.Null(session.TlsProtocol);

        var details = await worker.GetObjectDetailsAsync(fixture.Connection, children, token);
        Assert.Equal("id", Assert.Single(details.PrimaryKey).Name);
        Assert.Contains(details.Indexes, index => string.Equals(index.Name, "children_name", StringComparison.Ordinal));

        var count = await worker.CountQueryRowsAsync(fixture.Connection, "SELECT id, name FROM children",
            [new("id", "INTEGER", DatabaseValueKind.SignedInteger), new("name", "TEXT", DatabaseValueKind.Text)],
            [new("name", DatabaseFilterOperator.Equal, "ada")], token);
        Assert.Equal(2L, count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceLauncherRejectsHostRoutesBeforeStartingProcess(bool dynamicRoute)
    {
        using var fixture = new Fixture();
        var launches = 0;
        using var worker = new DatabaseOperationWorker(
            () => new DatabaseResultContentStore(fixture.ResultPath),
            workspaceLaunch: _ =>
            {
                launches++;
                return Task.FromResult(fixture.BackendLaunch());
            });
        var connection = fixture.Connection with
        {
            LocalRoutePort = dynamicRoute ? null : 44001,
            Route = dynamicRoute ? new DatabaseWorkerRoute(
                (_, _, _) => throw new InvalidOperationException("A host tunnel must not open."), CancellationToken.None) : null,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ListTablesAsync(connection, CancellationToken.None));
        Assert.Equal(0, launches);
    }

    [Theory]
    [InlineData("content-directory")]
    [InlineData("dynamic-route")]
    [InlineData("fixed-route")]
    public async Task PrivateBackendRejectsHostOnlyRequestFields(string field)
    {
        using var fixture = new Fixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var launch = fixture.BackendLaunch();
        var start = launch.StartInfo;
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        try
        {
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var request = new DatabaseOperationRequest(DatabaseWorkerOperation.ListTables,
                fixture.Connection with { LocalRoutePort = string.Equals(field, "fixed-route", StringComparison.Ordinal) ? 44001 : null },
                string.Equals(field, "content-directory", StringComparison.Ordinal) ? fixture.ResultPath : string.Empty,
                DynamicRoute: string.Equals(field, "dynamic-route", StringComparison.Ordinal));
            await DatabaseOperationProtocol.WriteMetadataAsync(process.StandardInput.BaseStream, request,
                DatabaseOperationJsonContext.Default.DatabaseOperationRequest, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await output;
            Assert.Equal(64, process.ExitCode);
            Assert.Empty(await errors);
            Assert.False(File.Exists(fixture.DatabasePath));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await launch.CleanupAsync();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ghostshell-metadata-test-");
        private readonly List<string> _scratchDirectories = [];
        public string DatabasePath => Path.Combine(_directory.FullName, "fixture.db");
        public string ResultPath => Path.Combine(_directory.FullName, "results");
        public DatabaseWorkerConnection Connection => new("sqlite",
            new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());

        public DatabaseOperationWorker Worker(bool privateWorkspace) => new(
            () => new DatabaseResultContentStore(ResultPath),
            new SelfReentryLaunch(DotnetPath(), [typeof(Program).Assembly.Location], DotnetPath()),
            workspaceLaunch: privateWorkspace ? _ => Task.FromResult(BackendLaunch()) : null);

        public DatabaseWorkspaceOperationLaunch BackendLaunch()
        {
            var backend = typeof(DatabaseMetadataWorkerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => string.Equals(attribute.Key, "DatabaseBackendPath", StringComparison.Ordinal)).Value;
            Assert.True(File.Exists(backend), $"The backend executable was not built: {backend}");
            var start = new ProcessStartInfo(DotnetPath());
            var operationId = Guid.NewGuid().ToString("N");
            DatabaseWorkspaceScratch.Prepare(operationId);
            using (var scratch = DatabaseWorkspaceScratch.Acquire(operationId)) { _scratchDirectories.Add(scratch.DirectoryPath); }
            start.ArgumentList.Add(backend!);
            start.ArgumentList.Add("database");
            start.ArgumentList.Add(operationId);
            return new(start, () => DatabaseWorkspaceScratch.CleanupAsync(operationId, CancellationToken.None));
        }

        private static string DotnetPath()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) { root = root.Parent; }
            return Path.Combine(root!.FullName, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        }

        public async Task SeedAsync()
        {
            await using var connection = new SqliteConnection(Connection.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE parents(id INTEGER PRIMARY KEY);
                CREATE TABLE children(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parents(id), name TEXT NOT NULL);
                CREATE INDEX children_name ON children(name);
                CREATE VIEW child_names AS SELECT name FROM children;
                INSERT INTO parents VALUES (1);
                INSERT INTO children VALUES (1, 1, 'ada'), (2, 1, 'grace'), (3, 1, 'ada');
                """;
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            foreach (var directory in _scratchDirectories)
            {
                if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
            }
            _directory.Delete(recursive: true);
        }
    }
}
