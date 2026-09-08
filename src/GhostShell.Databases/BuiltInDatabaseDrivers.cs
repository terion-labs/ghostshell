using System.Data.Common;
using ClickHouse.Client.ADO;
using DuckDB.NET.Data;
using FirebirdSql.Data.FirebirdClient;
using GhostShell.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace GhostShell.Databases;

/// <summary>
/// The drivers this build ships. Ids are durable: saved panels store them.
/// Engines that speak another engine's wire protocol (MariaDB, CockroachDB,
/// Redshift) get their own id and hint but reuse the compatible provider, so a
/// saved panel names what the user actually connected to.
/// </summary>
public static class BuiltInDatabaseDrivers
{
    public static IReadOnlyList<IDatabaseDriver> All { get; } =
    [
        new SqliteDatabaseDriver(),
        new PostgresFamilyDriver(
            "postgres",
            "PostgreSQL",
            "Host=localhost;Port=5432;Database=app;Username=postgres;Password=…",
            5432),
        new MySqlFamilyDriver(
            "mysql",
            "MySQL",
            "Server=localhost;Port=3306;Database=app;User ID=root;Password=…"),
        new MySqlFamilyDriver(
            "mariadb",
            "MariaDB",
            "Server=localhost;Port=3306;Database=app;User ID=root;Password=…"),
        new SqlServerDatabaseDriver(),
        new PostgresFamilyDriver(
            "cockroach",
            "CockroachDB",
            "Host=localhost;Port=26257;Database=app;Username=root;SSL Mode=Disable",
            26257),
        new PostgresFamilyDriver(
            "redshift",
            "Amazon Redshift",
            "Host=cluster.region.redshift.amazonaws.com;Port=5439;Database=app;Username=…;Password=…",
            5439),
        new DuckDbDatabaseDriver(),
        new OracleDatabaseDriver(),
        new FirebirdDatabaseDriver(),
        new ClickHouseDatabaseDriver(),
    ];
}

/// <summary>
/// File-based engines take a bare path in the viewer: "/data/app.db" (or
/// "~/app.db") becomes "Data Source=…" before the provider parses it. Anything
/// containing '=' is already a connection string and passes through.
/// </summary>
internal static class FileConnectionStrings
{
    public static string Normalize(string connectionString)
    {
        var value = connectionString.Trim();
        if (value.Length == 0 || value.Contains('='))
        {
            return connectionString;
        }

        if (value.StartsWith("~/", StringComparison.Ordinal))
        {
            value = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                value[2..]);
        }

        return $"Data Source={value}";
    }
}

internal sealed class SqliteDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "sqlite",
        "SQLite",
        "/path/to/database.db",
        IsFileBased: true);

    public DbConnection CreateConnection(string connectionString) =>
        SqliteInMemoryDatabases.TryCreateConnection(connectionString)
            ?? new SqliteConnection(connectionString);

    public string NormalizeConnectionString(string connectionString) =>
        connectionString.Contains(
            SqliteInMemoryDatabases.TokenPrefix,
            StringComparison.Ordinal)
            // A memory token is already exact; path normalization would only
            // mangle it.
            ? connectionString
            : FileConnectionStrings.Normalize(
                FileConnectionUrls.StripScheme(connectionString, "sqlite", "sqlite3"));

    public string ListTablesSql => """
        SELECT NULL, NULL, name, type FROM sqlite_master
        WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
        ORDER BY name;
        """;

    public string SqlCatalogDefaultsSql => "SELECT NULL, 'main';";

    public string ListRoutinesSql => """
        SELECT NULL, NULL, name,
               CASE type WHEN 'a' THEN 'aggregate'
                         WHEN 'w' THEN 'window'
                         ELSE 'scalar' END,
               name || '(' || CASE WHEN narg < 0 THEN '...'
                                    ELSE CAST(narg AS TEXT) || ' args' END || ')',
               NULL, NULL, NULL, NULL, NULL,
               0, CASE WHEN narg < 0 THEN 1 ELSE 0 END,
               CASE WHEN narg < 0 THEN 0 ELSE narg END,
               CASE WHEN narg < 0 THEN NULL ELSE narg END
        FROM pragma_function_list
        ORDER BY name, narg;
        """;

    public SqlCatalogCoverage RoutineCatalogCoverage => SqlCatalogCoverage.Complete;

    public string ListIntrinsicSymbolsSql => """
        SELECT name, 'keyword'
        FROM pragma_function_list
        WHERE builtin = 1
        ORDER BY name;
        """;

    public SqlCatalogCoverage IntrinsicCatalogCoverage => SqlCatalogCoverage.Complete;

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";

    public DatabaseEndpoint? GetEndpoint(string connectionString) => null;

    public string RewriteEndpoint(string connectionString, string host, int port) =>
        throw new InvalidOperationException(
            "SQLite databases are files and cannot be tunneled.");

    public DatabaseConnectionDetails ParseDetails(string connectionString)
    {
        var normalized = FileConnectionStrings.Normalize(connectionString);
        var builder = ConnectionDetailKeys.TryCreateBuilder(normalized);
        if (builder is null)
        {
            return new DatabaseConnectionDetails(FilePath: connectionString.Trim());
        }

        var path = ConnectionDetailKeys.Take(builder, ["Data Source", "DataSource"]);
        return new DatabaseConnectionDetails(
            FilePath: path,
            Options: builder.Count == 0 ? null : builder.ConnectionString);
    }

    public string BuildConnectionString(DatabaseConnectionDetails details)
    {
        if (string.IsNullOrWhiteSpace(details.Options))
        {
            return details.FilePath?.Trim() ?? string.Empty;
        }

        var builder = ConnectionDetailKeys.TryCreateBuilder(details.Options)
            ?? [];
        ConnectionDetailKeys.Set(builder, ["Data Source", "DataSource"], details.FilePath?.Trim());
        return builder.ConnectionString;
    }
}

/// <summary>PostgreSQL and the engines that speak its wire protocol.</summary>
internal sealed class PostgresFamilyDriver(
    string id,
    string displayName,
    string connectionStringHint,
    int defaultPort) : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        id,
        displayName,
        connectionStringHint,
        DefaultPort: defaultPort,
        CanListDatabases: true);

    public DbConnection CreateConnection(string connectionString) =>
        new NpgsqlConnection(connectionString);

    public DbConnection CreateRoutedConnection(string connectionString, string host, int port)
    {
        var logicalHost = new NpgsqlConnectionStringBuilder(connectionString).Host;
        if (string.IsNullOrWhiteSpace(logicalHost) || logicalHost.Contains(',', StringComparison.Ordinal))
        {
            throw new NotSupportedException("Routed PostgreSQL connections require one logical server host.");
        }

        return new NpgsqlConnection(RewriteEndpoint(connectionString, host, port))
        {
            SslClientAuthenticationOptionsCallback = options => options.TargetHost = logicalHost,
        };
    }

    public string NormalizeConnectionString(string connectionString) =>
        PostgresConnectionStrings.Normalize(connectionString);

    public string ListDatabasesSql => """
        SELECT datname FROM pg_database
        WHERE NOT datistemplate
        ORDER BY datname;
        """;

    public async ValueTask<DatabaseSessionInfo> DescribeSessionAsync(
        DbConnection connection,
        CancellationToken cancellationToken) =>
        new(
            DatabaseSessionProbes.TryGetServerVersion(connection),
            // pg_stat_ssl names the negotiated protocol for this backend; the
            // row's version is NULL on a plaintext connection. Redshift and
            // CockroachDB lack the view and answer with no fact.
            await DatabaseSessionProbes.TryQueryScalarAsync(
                connection,
                "SELECT version FROM pg_stat_ssl WHERE pid = pg_backend_pid();",
                0,
                cancellationToken).ConfigureAwait(false));

    public string ListTablesSql => """
        SELECT table_catalog, table_schema, table_name,
               CASE table_type WHEN 'VIEW' THEN 'view' ELSE 'table' END
        FROM information_schema.tables
        WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
        ORDER BY table_schema, table_name;
        """;

    public string SqlCatalogDefaultsSql =>
        "SELECT current_database(), current_schema();";

    // Cockroach exposes the required pg_proc shape (with NULL pg_get_function_*
    // compatibility results, handled by the format_type fallbacks below).
    // Redshift's catalog is not PostgreSQL-compatible enough to claim this
    // metadata; it intentionally falls back to Calcite's dialect library.
    public string? ListRoutinesSql => id is "postgres" or "cockroach"
        ? """
          SELECT current_database(), n.nspname, p.proname,
                 CASE p.prokind WHEN 'a' THEN 'aggregate'
                                WHEN 'w' THEN 'window'
                                WHEN 'p' THEN 'unknown'
                                ELSE CASE WHEN p.proretset THEN 'table'
                                          ELSE 'scalar' END END,
                 p.proname || '(' || COALESCE(
                     pg_get_function_identity_arguments(p.oid),
                     array_to_string(ARRAY(
                         SELECT format_type(input_type_oid, NULL)
                         FROM unnest(p.proargtypes::oid[])
                             AS input_type(input_type_oid)
                     ), ', ')
                 ) || ')',
                 COALESCE(
                     pg_get_function_result(p.oid),
                     format_type(p.prorettype, NULL)),
                 argument.ordinality,
                 CASE WHEN p.proargnames IS NULL THEN NULL
                      ELSE p.proargnames[argument.ordinality::integer] END,
                 format_type(argument.type_oid, NULL),
                 'in',
                 argument.ordinality > p.pronargs - p.pronargdefaults
                     - CASE WHEN p.provariadic <> 0 THEN 1 ELSE 0 END,
                 p.provariadic <> 0 AND argument.ordinality = p.pronargs,
                 GREATEST(
                     0,
                     p.pronargs - p.pronargdefaults
                         - CASE WHEN p.provariadic <> 0 THEN 1 ELSE 0 END),
                 CASE WHEN p.provariadic <> 0 THEN NULL ELSE p.pronargs END,
                 p.oid::text
          FROM pg_proc p
          JOIN pg_namespace n ON n.oid = p.pronamespace
          LEFT JOIN LATERAL
              unnest(p.proargtypes::oid[]) WITH ORDINALITY
              AS argument(type_oid, ordinality) ON TRUE
          WHERE n.nspname <> 'information_schema'
            AND n.nspname NOT LIKE 'pg_toast%'
            AND p.prokind <> 'p'
          ORDER BY CASE WHEN n.nspname = current_schema() THEN 0
                        WHEN n.nspname = 'pg_catalog' THEN 1 ELSE 2 END,
                   1, 2, 3, 5, 7;
          """
        : null;

    public SqlCatalogCoverage RoutineCatalogCoverage => id is "postgres" or "cockroach"
        ? SqlCatalogCoverage.Complete
        : SqlCatalogCoverage.None;

    public string? ListIntrinsicSymbolsSql => id is "postgres" or "cockroach"
        ? "SELECT word, 'keyword' FROM pg_get_keywords() ORDER BY word;"
        : null;

    public SqlCatalogCoverage IntrinsicCatalogCoverage => id is "postgres" or "cockroach"
        ? SqlCatalogCoverage.Complete
        : SqlCatalogCoverage.None;

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";

    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.Host)
            ? null
            : new DatabaseEndpoint(builder.Host, builder.Port);
    }

    public string RewriteEndpoint(string connectionString, string host, int port) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = host,
            Port = port,
        }.ConnectionString;

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["Host", "Server"],
        ["Port"],
        ["Database", "DB"],
        ["Username", "User ID", "UserName", "User Id"],
        ["Password"]);

    public DatabaseConnectionDetails ParseDetails(string connectionString) =>
        DetailKeys.Parse(connectionString);

    public string BuildConnectionString(DatabaseConnectionDetails details) =>
        DetailKeys.Build(details);
}

/// <summary>MySQL and MariaDB, which share MySqlConnector.</summary>
internal sealed class MySqlFamilyDriver(
    string id,
    string displayName,
    string connectionStringHint) : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        id,
        displayName,
        connectionStringHint,
        DefaultPort: 3306,
        CanListDatabases: true);

    public DbConnection CreateConnection(string connectionString) =>
        new MySqlConnection(connectionString);

    public DbConnection CreateRoutedConnection(string connectionString, string host, int port)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        if (builder.ServerRedirectionMode != MySqlServerRedirectionMode.Disabled)
        {
            throw new NotSupportedException("MySQL server redirection cannot open destinations outside the selected workspace relay.");
        }

        if (builder.SslMode == MySqlSslMode.VerifyFull)
        {
            var validator = new MySqlRelayCertificateValidator(builder.Server, builder.SslCa);
            builder.Server = host;
            builder.Port = checked((uint)port);
            // Required activates the provider callback; the callback enforces
            // VerifyFull, including CA trust and revocation, itself. Leaving
            // SslCa set would cause MySqlConnector to ignore that callback.
            builder.SslMode = MySqlSslMode.Required;
            builder.SslCa = string.Empty;
            builder.Pooling = false;
            return new MySqlConnection(builder.ConnectionString)
            {
                RemoteCertificateValidationCallback = validator.Validate,
            };
        }

        return new MySqlConnection(RewriteEndpoint(connectionString, host, port));
    }

    public string ListDatabasesSql => """
        SELECT schema_name FROM information_schema.schemata
        ORDER BY schema_name;
        """;

    public async ValueTask<DatabaseSessionInfo> DescribeSessionAsync(
        DbConnection connection,
        CancellationToken cancellationToken) =>
        new(
            DatabaseSessionProbes.TryGetServerVersion(connection),
            // SHOW answers in (Variable_name, Value) pairs; the fact is the
            // second column, and an empty value means a plaintext session.
            // This spelling predates performance_schema, so it holds across
            // every MySQL and MariaDB this driver can reach.
            await DatabaseSessionProbes.TryQueryScalarAsync(
                connection,
                "SHOW SESSION STATUS LIKE 'Ssl_version';",
                1,
                cancellationToken).ConfigureAwait(false));

    private static readonly Dictionary<string, string> UrlParameterNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // MySQL Shell's spelling of the one parameter a hosted MySQL URL
            // usually carries.
            ["ssl-mode"] = "SslMode",
            ["ssl-ca"] = "SslCa",
            ["ssl-cert"] = "SslCert",
            ["ssl-key"] = "SslKey",
        };

    public string NormalizeConnectionString(string connectionString)
    {
        if (ConnectionUrl.TryParse(connectionString, "mysql", "mariadb") is not { } url)
        {
            return connectionString;
        }

        var builder = new MySqlConnectionStringBuilder();
        if (url.Hosts.Length > 0)
        {
            builder.Server = url.Host ?? url.Hosts;
        }

        if (url.Port is { } port)
        {
            builder.Port = (uint)port;
        }

        if (url.Database is { } database)
        {
            builder.Database = database;
        }

        if (url.Username is { } username)
        {
            builder.UserID = username;
        }

        if (url.Password is { } password)
        {
            builder.Password = password;
        }

        url.ApplyParameters(builder, "MySQL", UrlParameterNames);
        return builder.ConnectionString;
    }

    public string ListTablesSql => """
        SELECT NULL, table_schema, table_name,
               CASE table_type WHEN 'VIEW' THEN 'view' ELSE 'table' END
        FROM information_schema.tables
        WHERE table_schema = DATABASE()
        ORDER BY table_name;
        """;

    public string SqlCatalogDefaultsSql => "SELECT NULL, DATABASE();";

    public string ListRoutinesSql => """
        SELECT NULL, routine.routine_schema, routine.routine_name,
               'scalar', routine.routine_name,
               COALESCE(routine.dtd_identifier, routine.data_type),
               parameter.ordinal_position, parameter.parameter_name,
               COALESCE(parameter.dtd_identifier, parameter.data_type),
               LOWER(parameter.parameter_mode),
               0, 0, NULL, NULL
        FROM information_schema.routines routine
        LEFT JOIN information_schema.parameters parameter
          ON parameter.specific_schema = routine.routine_schema
         AND parameter.specific_name = routine.specific_name
         AND parameter.ordinal_position > 0
        WHERE routine.routine_schema = DATABASE()
          AND routine.routine_type = 'FUNCTION'
        ORDER BY 2, 3, 5, 7;
        """;

    public SqlCatalogCoverage RoutineCatalogCoverage =>
        SqlCatalogCoverage.UserDefinedOnly;

    // HELP is privilege-safe for ordinary MySQL/MariaDB users and enumerates
    // the server's installed help topics without selecting mysql.help_topic.
    public string ListIntrinsicSymbolsSql => "HELP '%'";

    public SqlCatalogCoverage IntrinsicCatalogCoverage => SqlCatalogCoverage.Complete;

    public string QuoteIdentifier(string identifier) =>
        $"`{identifier.Replace("`", "``")}`";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";

    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.Server)
            ? null
            : new DatabaseEndpoint(builder.Server, (int)builder.Port);
    }

    public string RewriteEndpoint(string connectionString, string host, int port) =>
        new MySqlConnectionStringBuilder(connectionString)
        {
            Server = host,
            Port = (uint)port,
        }.ConnectionString;

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["Server", "Host", "Data Source"],
        ["Port"],
        ["Database"],
        ["User ID", "UserID", "User", "Username", "Uid"],
        ["Password", "Pwd"]);

    public DatabaseConnectionDetails ParseDetails(string connectionString) =>
        DetailKeys.Parse(connectionString);

    public string BuildConnectionString(DatabaseConnectionDetails details) =>
        DetailKeys.Build(details);
}

internal sealed class SqlServerDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "sqlserver",
        "SQL Server",
        "Server=localhost,1433;Database=app;User ID=sa;Password=…;TrustServerCertificate=True",
        DefaultPort: 1433,
        CanListDatabases: true);

    public DbConnection CreateConnection(string connectionString) =>
        new SqlConnection(connectionString);

    public DbConnection CreateRoutedConnection(string connectionString, string host, int port) =>
        throw new NotSupportedException("Routed SQL Server requires a captured endpoint transport; a fixed loopback port cannot safely handle server redirects.");

    public DbConnection CreateRoutedConnection(string connectionString, string host, int port, DatabaseConnectionRoute route) =>
        new SqlConnection(connectionString) { TcpTransport = route.SqlTransport };

    public string ListDatabasesSql => """
        SELECT name FROM sys.databases
        WHERE state = 0
        ORDER BY name;
        """;

    public async ValueTask<DatabaseSessionInfo> DescribeSessionAsync(
        DbConnection connection,
        CancellationToken cancellationToken) =>
        new(
            DatabaseSessionProbes.TryGetServerVersion(connection),
            // The server says whether the session is encrypted but not which
            // protocol carried it, so the fact is the family name only.
            string.Equals(
                await DatabaseSessionProbes.TryQueryScalarAsync(
                    connection,
                    "SELECT encrypt_option FROM sys.dm_exec_connections "
                    + "WHERE session_id = @@SPID;",
                    0,
                    cancellationToken).ConfigureAwait(false),
                "TRUE",
                StringComparison.OrdinalIgnoreCase)
                ? "TLS"
                : null);

    /// <summary>
    /// <c>sqlserver://host:1433;database=app</c> is how JDBC writes it and how
    /// the tools that grew up around JDBC repeat it; <c>mssql://</c> with a
    /// path is the other spelling. Both land in one DataSource, because that is
    /// where this provider keeps the address.
    /// </summary>
    public string NormalizeConnectionString(string connectionString)
    {
        if (ConnectionUrl.TryParse(connectionString, "sqlserver", "mssql") is not { } url)
        {
            return connectionString;
        }

        var builder = new SqlConnectionStringBuilder();
        if (url.Hosts.Length > 0)
        {
            builder.DataSource = url.Port is { } port
                ? $"{url.Host},{port}"
                : url.Hosts;
        }

        if (url.Database is { } database)
        {
            builder.InitialCatalog = database;
        }

        if (url.Username is { } username)
        {
            builder.UserID = username;
        }

        if (url.Password is { } password)
        {
            builder.Password = password;
        }

        url.ApplyParameters(builder, "SQL Server");
        return builder.ConnectionString;
    }

    public string ListTablesSql => """
        SELECT TABLE_CATALOG, TABLE_SCHEMA, TABLE_NAME,
               CASE TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END
        FROM INFORMATION_SCHEMA.TABLES
        ORDER BY TABLE_SCHEMA, TABLE_NAME;
        """;

    public string SqlCatalogDefaultsSql => "SELECT DB_NAME(), SCHEMA_NAME();";

    public string ListRoutinesSql => """
        SELECT DB_NAME(), schema_name(object.schema_id), object.name,
               CASE WHEN object.type IN ('IF', 'TF', 'FT') THEN 'table'
                    WHEN object.type = 'AF' THEN 'aggregate'
                    ELSE 'scalar' END,
               object.name,
               CASE WHEN object.type IN ('IF', 'TF', 'FT') THEN 'table'
                    ELSE type_name(return_value.user_type_id) END,
               parameter.parameter_id, parameter.name,
               type_name(parameter.user_type_id),
               CASE WHEN parameter.is_output = 1 THEN 'out' ELSE 'in' END,
               parameter.has_default_value, 0, NULL, NULL
        FROM sys.objects object
        LEFT JOIN sys.parameters return_value
          ON return_value.object_id = object.object_id
         AND return_value.parameter_id = 0
        LEFT JOIN sys.parameters parameter
          ON parameter.object_id = object.object_id
         AND parameter.parameter_id > 0
        WHERE object.type IN ('FN', 'IF', 'TF', 'FS', 'FT', 'AF')
          AND object.is_ms_shipped = 0
        ORDER BY 1, 2, 3, 5, 7;
        """;

    public SqlCatalogCoverage RoutineCatalogCoverage =>
        SqlCatalogCoverage.UserDefinedOnly;

    public string QuoteIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]")}]";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT TOP ({limit}) * FROM {QuoteIdentifier(tableName)};";

    // SQL Server addresses are "host" or "host,port" in DataSource.
    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var source = new SqlConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        if (source.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            source = source[4..];
        }

        if (source.Contains('\\', StringComparison.Ordinal))
        {
            throw new NotSupportedException("Routed SQL Server connections require an explicit TCP host and port, not a named instance or pipe.");
        }

        var parts = source.Split(',', 2);
        return new DatabaseEndpoint(
            parts[0].Trim(),
            parts.Length == 2 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var port) ? port : 1433);
    }

    public string RewriteEndpoint(string connectionString, string host, int port)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(builder.FailoverPartner))
        {
            throw new NotSupportedException("SQL Server failover partners require a route-aware driver transport and cannot bypass the workspace relay.");
        }
        var endpoint = GetEndpoint(connectionString)
            ?? throw new InvalidOperationException("SQL Server requires a logical server host.");
        if (string.IsNullOrWhiteSpace(builder.HostNameInCertificate))
        {
            builder.HostNameInCertificate = endpoint.Host;
        }

        builder.DataSource = $"{host},{port}";
        return builder.ConnectionString;
    }

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["Server", "Data Source", "Address", "Addr", "Network Address"],
        [],
        ["Database", "Initial Catalog"],
        ["User ID", "UID"],
        ["Password", "PWD"]);

    public DatabaseConnectionDetails ParseDetails(string connectionString)
    {
        var details = DetailKeys.Parse(connectionString);
        if (details.Host is { } packed)
        {
            var parts = packed.Split(',', 2);
            details = details with
            {
                Host = parts[0].Trim(),
                Port = parts.Length == 2 && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var port) ? port
                    : details.Port,
            };
        }

        return details;
    }

    public string BuildConnectionString(DatabaseConnectionDetails details) =>
        DetailKeys.Build(details with
        {
            Host = details.Port is { } port && !string.IsNullOrWhiteSpace(details.Host)
                ? $"{details.Host},{port}"
                : details.Host,
            Port = null,
        });
}

internal sealed class DuckDbDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "duckdb",
        "DuckDB",
        "/path/to/analytics.duckdb",
        IsFileBased: true);

    public DbConnection CreateConnection(string connectionString) =>
        new DuckDBConnection(connectionString);

    public string NormalizeConnectionString(string connectionString) =>
        FileConnectionStrings.Normalize(
            FileConnectionUrls.StripScheme(connectionString, "duckdb"));

    public string ListTablesSql => """
        SELECT table_catalog, table_schema, table_name,
               CASE table_type WHEN 'VIEW' THEN 'view' ELSE 'table' END
        FROM information_schema.tables
        WHERE table_schema NOT IN ('information_schema', 'pg_catalog')
        ORDER BY table_schema, table_name;
        """;

    public string SqlCatalogDefaultsSql =>
        "SELECT current_database(), current_schema();";

    public string ListRoutinesSql => """
        SELECT database_name, schema_name, function_name,
               CASE function_type WHEN 'aggregate' THEN 'aggregate'
                                  WHEN 'table' THEN 'table'
                                  ELSE 'scalar' END,
               function_name || '(' || array_to_string(parameter_types, ', ') || ')',
               return_type, NULL, NULL, NULL, NULL, 0,
               CASE WHEN varargs IS NULL THEN 0 ELSE 1 END,
               array_length(parameter_types),
               CASE WHEN varargs IS NULL THEN array_length(parameter_types)
                    ELSE NULL END
        FROM duckdb_functions()
        ORDER BY internal, 1, 2, 3, 5;
        """;

    public SqlCatalogCoverage RoutineCatalogCoverage => SqlCatalogCoverage.Complete;

    public string ListIntrinsicSymbolsSql => """
        SELECT keyword_name, 'keyword'
        FROM duckdb_keywords()
        ORDER BY keyword_name;
        """;

    // duckdb_keywords() is authoritative for lexical keywords, but DuckDB does
    // not publish context-sensitive bare values such as CURRENT_TIMESTAMP in
    // either that catalog or duckdb_functions(). Keep the positive evidence,
    // while making the missing intrinsic coverage visible to the editor.
    public SqlCatalogCoverage IntrinsicCatalogCoverage => SqlCatalogCoverage.Partial;

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";

    public DatabaseEndpoint? GetEndpoint(string connectionString) => null;

    public string RewriteEndpoint(string connectionString, string host, int port) =>
        throw new InvalidOperationException(
            "DuckDB databases are files and cannot be tunneled.");

    public DatabaseConnectionDetails ParseDetails(string connectionString)
    {
        var normalized = FileConnectionStrings.Normalize(connectionString);
        var builder = ConnectionDetailKeys.TryCreateBuilder(normalized);
        if (builder is null)
        {
            return new DatabaseConnectionDetails(FilePath: connectionString.Trim());
        }

        var path = ConnectionDetailKeys.Take(builder, ["Data Source", "DataSource"]);
        return new DatabaseConnectionDetails(
            FilePath: path,
            Options: builder.Count == 0 ? null : builder.ConnectionString);
    }

    public string BuildConnectionString(DatabaseConnectionDetails details)
    {
        if (string.IsNullOrWhiteSpace(details.Options))
        {
            return details.FilePath?.Trim() ?? string.Empty;
        }

        var builder = ConnectionDetailKeys.TryCreateBuilder(details.Options)
            ?? [];
        ConnectionDetailKeys.Set(builder, ["Data Source", "DataSource"], details.FilePath?.Trim());
        return builder.ConnectionString;
    }
}

internal sealed class OracleDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "oracle",
        "Oracle",
        "Data Source=localhost:1521/FREEPDB1;User Id=app;Password=…",
        DefaultPort: 1521,
        // Oracle connects to a service, and enumerating other services is a
        // listener question SQL cannot ask.
        DatabaseLabel: "Service");

    public DbConnection CreateConnection(string connectionString) =>
        new OracleConnection(connectionString);

    public async ValueTask<DatabaseSessionInfo> DescribeSessionAsync(
        DbConnection connection,
        CancellationToken cancellationToken) =>
        new(
            DatabaseSessionProbes.TryGetServerVersion(connection),
            // The session context names its transport: tcps is TLS, tcp is not.
            string.Equals(
                await DatabaseSessionProbes.TryQueryScalarAsync(
                    connection,
                    "SELECT SYS_CONTEXT('USERENV', 'NETWORK_PROTOCOL') FROM DUAL",
                    0,
                    cancellationToken).ConfigureAwait(false),
                "tcps",
                StringComparison.OrdinalIgnoreCase)
                ? "TLS"
                : null);

    /// <summary>
    /// <c>oracle://user:password@host:1521/FREEPDB1</c>, which is the URL form
    /// the tooling around Oracle settled on. It becomes Easy Connect —
    /// host:port/service — because that is the one address form this provider
    /// can also tunnel.
    /// </summary>
    public string NormalizeConnectionString(string connectionString)
    {
        if (ConnectionUrl.TryParse(connectionString, "oracle") is not { } url)
        {
            return connectionString;
        }

        var builder = new OracleConnectionStringBuilder();
        if (url.Hosts.Length > 0)
        {
            var address = url.Port is { } port ? $"{url.Host}:{port}" : url.Hosts;
            builder.DataSource = url.Database is { } service
                ? $"{address}/{service}"
                : address;
        }

        if (url.Username is { } username)
        {
            builder.UserID = username;
        }

        if (url.Password is { } password)
        {
            builder.Password = password;
        }

        url.ApplyParameters(builder, "Oracle");
        return builder.ConnectionString;
    }

    public string ListTablesSql => """
        SELECT NULL, USER, table_name, 'table' FROM user_tables
        UNION ALL
        SELECT NULL, USER, view_name, 'view' FROM user_views
        ORDER BY 3
        """;

    public string SqlCatalogDefaultsSql =>
        "SELECT NULL, SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') FROM DUAL";

    public string ListRoutinesSql => """
        SELECT NULL, procedure.owner, procedure.object_name,
               'scalar',
               procedure.object_name || '(' || COALESCE((
                   SELECT LISTAGG(signature_argument.data_type, ', ')
                              WITHIN GROUP (ORDER BY signature_argument.position)
                   FROM all_arguments signature_argument
                   WHERE signature_argument.owner = procedure.owner
                     AND signature_argument.object_name = procedure.object_name
                     AND signature_argument.subprogram_id = procedure.subprogram_id
                     AND signature_argument.position > 0
                     AND signature_argument.data_level = 0
               ), '') || ')',
               return_value.data_type,
               argument.position, argument.argument_name, argument.data_type,
               CASE argument.in_out WHEN 'IN' THEN 'in'
                                    WHEN 'OUT' THEN 'out'
                                    WHEN 'IN/OUT' THEN 'inout'
                                    ELSE 'unknown' END,
               CASE argument.defaulted WHEN 'Y' THEN 1 ELSE 0 END,
               0, NULL, NULL
        FROM all_procedures procedure
        LEFT JOIN all_arguments return_value
          ON return_value.owner = procedure.owner
         AND return_value.object_name = procedure.object_name
         AND return_value.subprogram_id = procedure.subprogram_id
         AND return_value.position = 0
         AND return_value.data_level = 0
        LEFT JOIN all_arguments argument
          ON argument.owner = procedure.owner
         AND argument.object_name = procedure.object_name
         AND argument.subprogram_id = procedure.subprogram_id
         AND argument.position > 0
         AND argument.data_level = 0
        WHERE procedure.owner = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')
          AND procedure.object_type = 'FUNCTION'
          AND procedure.procedure_name IS NULL
        ORDER BY 2, 3, 5, 7
        """;

    public SqlCatalogCoverage RoutineCatalogCoverage =>
        SqlCatalogCoverage.UserDefinedOnly;

    // V$SQLFN_METADATA positively proves callable SQL functions. Oracle's
    // reserved-word catalog cannot prove expression validity (for example it
    // lists CURRENT_TIME, which Oracle rejects as an identifier), so it must
    // not be used as intrinsic evidence. Bare-value coverage therefore stays
    // visibly partial instead of manufacturing false completions.
    public string ListIntrinsicSymbolsSql =>
        "SELECT name, 'keyword' FROM v$sqlfn_metadata ORDER BY name";

    public SqlCatalogCoverage IntrinsicCatalogCoverage => SqlCatalogCoverage.Partial;

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} FETCH FIRST {limit} ROWS ONLY";

    // Only the EZ Connect form "host[:port][/service]" can be tunneled; a TNS
    // alias resolves outside the connection string.
    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var source = new OracleConnectionStringBuilder(connectionString).DataSource;
        if (string.IsNullOrWhiteSpace(source) || source.StartsWith('(')
            || source.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        var slash = source.IndexOf('/', StringComparison.Ordinal);
        var address = slash < 0 ? source : source[..slash];
        var colon = address.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? new DatabaseEndpoint(address.Trim(), 1521)
            : new DatabaseEndpoint(
                address[..colon].Trim(),
                int.TryParse(address[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture, out var port) ? port : 1521);
    }

    public string RewriteEndpoint(string connectionString, string host, int port)
    {
        var builder = new OracleConnectionStringBuilder(connectionString);
        var source = builder.DataSource;
        var slash = source.IndexOf('/', StringComparison.Ordinal);
        builder.DataSource = slash < 0
            ? $"{host}:{port}"
            : $"{host}:{port}{source[slash..]}";
        return builder.ConnectionString;
    }

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["Data Source", "DataSource"],
        [],
        [],
        ["User Id", "UserID", "User"],
        ["Password"]);

    // The dialog's Database field carries the EZ Connect service name.
    public DatabaseConnectionDetails ParseDetails(string connectionString)
    {
        var details = DetailKeys.Parse(connectionString);
        if (details.Host is { } packed && !packed.StartsWith('('))
        {
            var slash = packed.IndexOf('/', StringComparison.Ordinal);
            var address = slash < 0 ? packed : packed[..slash];
            var colon = address.IndexOf(':', StringComparison.Ordinal);
            details = details with
            {
                Host = colon < 0 ? address.Trim() : address[..colon].Trim(),
                Port = colon >= 0 && int.TryParse(address[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture, out var port) ? port
                    : null,
                Database = slash < 0 ? null : packed[(slash + 1)..].Trim(),
            };
        }

        return details;
    }

    public string BuildConnectionString(DatabaseConnectionDetails details)
    {
        var address = details.Port is { } port
            ? $"{details.Host}:{port}"
            : details.Host;
        var packed = string.IsNullOrWhiteSpace(details.Database)
            ? address
            : $"{address}/{details.Database}";
        return DetailKeys.Build(details with
        {
            Host = packed,
            Port = null,
            Database = null,
        });
    }
}

internal sealed class FirebirdDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "firebird",
        "Firebird",
        "DataSource=localhost;Database=/path/to/app.fdb;User=SYSDBA;Password=…",
        DefaultPort: 3050,
        // A Firebird database is a server-side file path; there is nothing to
        // enumerate over the wire.
        DatabaseLabel: "Database path");

    public DbConnection CreateConnection(string connectionString) =>
        new FbConnection(connectionString);

    /// <summary>
    /// <c>firebird://user:password@host:3050//var/db/app.fdb</c>. The database
    /// is a path on the server, so what follows the host is kept whole rather
    /// than read as a name.
    /// </summary>
    public string NormalizeConnectionString(string connectionString)
    {
        if (ConnectionUrl.TryParse(connectionString, "firebird", "firebirdsql")
            is not { } url)
        {
            return connectionString;
        }

        var builder = new FbConnectionStringBuilder();
        if (url.Hosts.Length > 0)
        {
            builder.DataSource = url.Host ?? url.Hosts;
        }

        if (url.Port is { } port)
        {
            builder.Port = port;
        }

        if (url.Database is { } database)
        {
            builder.Database = database;
        }

        if (url.Username is { } username)
        {
            builder.UserID = username;
        }

        if (url.Password is { } password)
        {
            builder.Password = password;
        }

        url.ApplyParameters(builder, "Firebird");
        return builder.ConnectionString;
    }

    public string ListTablesSql => """
        SELECT NULL, NULL, TRIM(rdb$relation_name),
               CAST(
                   CASE WHEN rdb$relation_type = 1 THEN 'view' ELSE 'table' END
                   AS VARCHAR(5))
        FROM rdb$relations
        WHERE COALESCE(rdb$system_flag, 0) = 0
        ORDER BY 3
        """;

    public string ListRoutinesSql => """
        SELECT NULL, NULL, TRIM(routine.rdb$function_name),
               'scalar', TRIM(routine.rdb$function_name),
               CAST(CASE return_value.rdb$field_type
                        WHEN 7 THEN 'smallint' WHEN 8 THEN 'integer'
                        WHEN 10 THEN 'float' WHEN 12 THEN 'date'
                        WHEN 13 THEN 'time' WHEN 14 THEN 'char'
                        WHEN 16 THEN 'bigint' WHEN 23 THEN 'boolean'
                        WHEN 27 THEN 'double precision'
                        WHEN 28 THEN 'time with time zone'
                        WHEN 29 THEN 'timestamp with time zone'
                        WHEN 35 THEN 'timestamp' WHEN 37 THEN 'varchar'
                        WHEN 261 THEN 'blob' ELSE 'unknown' END AS VARCHAR(64)),
               arg.rdb$argument_position,
               TRIM(arg.rdb$argument_name),
               CAST(CASE arg.rdb$field_type
                        WHEN 7 THEN 'smallint' WHEN 8 THEN 'integer'
                        WHEN 10 THEN 'float' WHEN 12 THEN 'date'
                        WHEN 13 THEN 'time' WHEN 14 THEN 'char'
                        WHEN 16 THEN 'bigint' WHEN 23 THEN 'boolean'
                        WHEN 27 THEN 'double precision'
                        WHEN 28 THEN 'time with time zone'
                        WHEN 29 THEN 'timestamp with time zone'
                        WHEN 35 THEN 'timestamp' WHEN 37 THEN 'varchar'
                        WHEN 261 THEN 'blob' ELSE 'unknown' END AS VARCHAR(64)),
               'in',
               CASE WHEN arg.rdb$default_source IS NULL THEN 0 ELSE 1 END,
               0, NULL, NULL
        FROM rdb$functions routine
        LEFT JOIN rdb$function_arguments return_value
          ON return_value.rdb$function_name = routine.rdb$function_name
         AND return_value.rdb$package_name IS NOT DISTINCT FROM routine.rdb$package_name
         AND return_value.rdb$argument_position = routine.rdb$return_argument
        LEFT JOIN rdb$function_arguments arg
          ON arg.rdb$function_name = routine.rdb$function_name
         AND arg.rdb$package_name IS NOT DISTINCT FROM routine.rdb$package_name
         AND arg.rdb$argument_position <> routine.rdb$return_argument
        WHERE COALESCE(routine.rdb$system_flag, 0) = 0
          AND routine.rdb$package_name IS NULL
        ORDER BY 3, 5, 7
        """;

    public SqlCatalogCoverage RoutineCatalogCoverage =>
        SqlCatalogCoverage.UserDefinedOnly;

    public string ListIntrinsicSymbolsSql => """
        SELECT TRIM(rdb$keyword_name), 'keyword'
        FROM rdb$keywords
        ORDER BY rdb$keyword_name
        """;

    public SqlCatalogCoverage IntrinsicCatalogCoverage => SqlCatalogCoverage.Complete;

    public string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT FIRST {limit} * FROM {QuoteIdentifier(tableName)}";

    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var builder = new FbConnectionStringBuilder(connectionString);
        return string.IsNullOrWhiteSpace(builder.DataSource)
            ? null
            : new DatabaseEndpoint(builder.DataSource, builder.Port);
    }

    public string RewriteEndpoint(string connectionString, string host, int port) =>
        new FbConnectionStringBuilder(connectionString)
        {
            DataSource = host,
            Port = port,
        }.ConnectionString;

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["DataSource", "Data Source", "Server", "Host"],
        ["Port", "Port Number"],
        ["Database", "Initial Catalog"],
        ["User", "User ID", "UserID"],
        ["Password"]);

    public DatabaseConnectionDetails ParseDetails(string connectionString) =>
        DetailKeys.Parse(connectionString);

    public string BuildConnectionString(DatabaseConnectionDetails details) =>
        DetailKeys.Build(details);
}

internal sealed class ClickHouseDatabaseDriver : IDatabaseDriver
{
    public DatabaseDriverDescriptor Descriptor { get; } = new(
        "clickhouse",
        "ClickHouse",
        "Host=localhost;Port=8123;Database=default;Username=default;Password=…",
        DefaultPort: 8123,
        CanListDatabases: true);

    public DbConnection CreateConnection(string connectionString) =>
        new ClickHouseConnection(connectionString);

    public DbConnection CreateRoutedConnection(string connectionString, string host, int port) =>
        new RoutedClickHouseConnection(connectionString, host, port);

    public string ListDatabasesSql => """
        SELECT name FROM system.databases
        ORDER BY name;
        """;

    /// <summary>
    /// <c>clickhouse://user:password@host:8123/database</c>, as clickhouse-client
    /// and the JDBC driver write it.
    /// </summary>
    public string NormalizeConnectionString(string connectionString)
    {
        if (ConnectionUrl.TryParse(connectionString, "clickhouse") is not { } url)
        {
            return connectionString;
        }

        var builder = new System.Data.Common.DbConnectionStringBuilder();
        if (url.Hosts.Length > 0)
        {
            builder["Host"] = url.Host ?? url.Hosts;
        }

        if (url.Port is { } port)
        {
            builder["Port"] = port;
        }

        if (url.Database is { } database)
        {
            builder["Database"] = database;
        }

        if (url.Username is { } username)
        {
            builder["Username"] = username;
        }

        if (url.Password is { } password)
        {
            builder["Password"] = password;
        }

        url.ApplyParameters(builder, "ClickHouse");
        return builder.ConnectionString;
    }

    public string ListTablesSql => """
        SELECT NULL, database, name,
               CASE WHEN engine = 'View' THEN 'view' ELSE 'table' END
        FROM system.tables
        WHERE database = currentDatabase()
        ORDER BY name;
        """;

    public string SqlCatalogDefaultsSql => "SELECT NULL, currentDatabase();";

    public string ListRoutinesSql => """
        SELECT NULL, NULL, name,
               if(is_aggregate, 'aggregate', 'scalar'),
               concat(name, '(...)'),
               NULL, NULL, NULL, NULL, NULL,
               0, 0, 0, NULL
        FROM system.functions
        ORDER BY name;
        """;

    public SqlCatalogCoverage RoutineCatalogCoverage => SqlCatalogCoverage.Complete;

    public string ListIntrinsicSymbolsSql => """
        SELECT name, 'keyword'
        FROM system.functions
        ORDER BY name;
        """;

    public SqlCatalogCoverage IntrinsicCatalogCoverage => SqlCatalogCoverage.Complete;

    public string QuoteIdentifier(string identifier) =>
        $"`{identifier.Replace("`", "``")}`";

    public string BuildPreviewQuery(string tableName, int limit) =>
        $"SELECT * FROM {QuoteIdentifier(tableName)} LIMIT {limit};";

    public DatabaseEndpoint? GetEndpoint(string connectionString)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder
        {
            ConnectionString = connectionString,
        };
        if (!builder.TryGetValue("Host", out var host)
            || string.IsNullOrWhiteSpace(host as string))
        {
            return null;
        }

        var port = builder.TryGetValue("Port", out var value)
            && int.TryParse(value as string, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed
                : 8123;
        return new DatabaseEndpoint((string)host, port);
    }

    public string RewriteEndpoint(string connectionString, string host, int port)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder
        {
            ConnectionString = connectionString,
        };
        builder["Host"] = host;
        builder["Port"] = port;
        return builder.ConnectionString;
    }

    private static readonly ConnectionDetailKeys DetailKeys = new(
        ["Host"],
        ["Port"],
        ["Database"],
        ["Username", "User"],
        ["Password"]);

    public DatabaseConnectionDetails ParseDetails(string connectionString) =>
        DetailKeys.Parse(connectionString);

    public string BuildConnectionString(DatabaseConnectionDetails details) =>
        DetailKeys.Build(details);
}
