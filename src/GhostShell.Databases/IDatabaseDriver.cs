using System.Data.Common;
using GhostShell.Application;

namespace GhostShell.Databases;

/// <summary>
/// One ADO.NET-backed database engine. Everything engine-specific — the
/// provider factory, schema catalog queries, identifier quoting, and paging
/// syntax — lives behind this boundary so the client stays generic.
/// </summary>
public interface IDatabaseDriver
{
    DatabaseDriverDescriptor Descriptor { get; }

    DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Maps friendly input onto the provider's syntax before anything parses
    /// it — file engines accept a bare path here. The default keeps the input.
    /// </summary>
    string NormalizeConnectionString(string connectionString) => connectionString;

    /// <summary>
    /// A statement returning (catalog, schema, name, kind) rows for tables and
    /// views, ordered by qualified name. Missing qualification components are
    /// returned as SQL NULL; kind is the literal 'table' or 'view'.
    /// </summary>
    string ListTablesSql { get; }

    /// <summary>
    /// A statement returning one database name per row, for the database
    /// selector — or null when the engine has nothing to enumerate: file
    /// engines open exactly one file, Firebird names a server-side path, and
    /// Oracle's services resolve outside SQL. The descriptor's
    /// CanListDatabases must agree with this member.
    /// </summary>
    string? ListDatabasesSql => null;

    /// <summary>
    /// A statement returning the session's current (catalog, schema) in one
    /// row, or null when the engine has no meaningful namespace pair. SQL
    /// intelligence uses the live session values instead of guessing common
    /// names such as public or dbo.
    /// </summary>
    string? SqlCatalogDefaultsSql => null;

    /// <summary>
    /// Optional routine metadata query, ordered by qualified routine,
    /// signature, and parameter ordinal. It returns: catalog, schema, name,
    /// kind, signature, return type, parameter ordinal, parameter name,
    /// parameter type, parameter mode, optional flag, variadic flag, minimum
    /// argument count, and maximum argument count. An optional fifteenth
    /// column may carry a stable server-side routine identity used only to
    /// keep overload parameter rows separate while reading metadata. A SQL
    /// NULL minimum asks the client to derive arity from parameter rows; when
    /// minimum is present, a NULL maximum means unbounded. Only routines
    /// callable inside SQL expressions belong here; unsupported catalogs
    /// return null.
    /// </summary>
    string? ListRoutinesSql => null;

    /// <summary>
    /// Describes whether a successful routine query is authoritative for
    /// server-callable functions. The client downgrades this to Partial when
    /// extraction fails or reaches a safety boundary.
    /// </summary>
    SqlCatalogCoverage RoutineCatalogCoverage => SqlCatalogCoverage.None;

    /// <summary>
    /// Optional two-column query returning intrinsic symbol name and kind.
    /// These rows corroborate known Calcite operators only; they never create
    /// arbitrary callable functions.
    /// </summary>
    string? ListIntrinsicSymbolsSql => null;

    SqlCatalogCoverage IntrinsicCatalogCoverage => SqlCatalogCoverage.None;

    /// <summary>
    /// Session facts read from an already-open connection. The default answers
    /// with the provider's server version and no TLS fact; engines that can
    /// name their negotiated protocol override. A fact the server will not
    /// give up is null, never an exception — the connection itself is fine.
    /// </summary>
    ValueTask<DatabaseSessionInfo> DescribeSessionAsync(
        DbConnection connection,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new DatabaseSessionInfo(
            DatabaseSessionProbes.TryGetServerVersion(connection)));

    string QuoteIdentifier(string identifier);

    string BuildPreviewQuery(string tableName, int limit);

    /// <summary>Decomposes a connection string for the details dialog.</summary>
    DatabaseConnectionDetails ParseDetails(string connectionString);

    /// <summary>Recomposes dialog fields into this engine's connection string.</summary>
    string BuildConnectionString(DatabaseConnectionDetails details);
}
