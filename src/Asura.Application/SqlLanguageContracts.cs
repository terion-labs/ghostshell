namespace Asura.Application;

/// <summary>
/// Opens isolated SQL-intelligence sessions from detached database metadata.
/// Implementations never receive database credentials or a live connection.
/// </summary>
public interface ISqlLanguageService
{
    /// <summary>Whether a worker executable is installed for this process.</summary>
    bool IsAvailable { get; }

    Task<ISqlLanguageSession> OpenSessionAsync(
        SqlCatalogSnapshot catalog,
        CancellationToken cancellationToken);
}

/// <summary>One replaceable catalog and its SQL completion/diagnostic state.</summary>
public interface ISqlLanguageSession : IAsyncDisposable
{
    /// <summary>
    /// False when the optional language worker is not installed or has stopped.
    /// Calls remain safe and return empty results in that state.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// True when a stopped worker may be started again by a later interaction.
    /// Permanent initialization/catalog failures remain false until the owner
    /// replaces the session.
    /// </summary>
    bool CanRetry => false;

    /// <summary>A bounded user-facing reason when initialization has failed.</summary>
    string? UnavailableReason => null;

    Task<SqlCompletionResult> CompleteAsync(
        string sql,
        int cursorOffset,
        CancellationToken cancellationToken) =>
        CompleteAsync(
            sql,
            cursorOffset,
            SqlCompletionContext.Empty,
            cancellationToken);

    /// <summary>
    /// Completes one editor snapshot with request-scoped UI context. The
    /// preferred object is only a completion hint; it never changes SQL
    /// validation, catalog defaults, or the worker session's state.
    /// </summary>
    Task<SqlCompletionResult> CompleteAsync(
        string sql,
        int cursorOffset,
        SqlCompletionContext context,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SqlDiagnostic>> DiagnoseAsync(
        string sql,
        CancellationToken cancellationToken);

    Task UpdateCatalogAsync(
        SqlCatalogSnapshot catalog,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider-neutral metadata sent to the language worker. DriverId selects
/// parser quoting/casing rules; object and column names retain their exact case.
/// </summary>
public sealed record SqlCatalogSnapshot(
    string DriverId,
    string? DefaultCatalog,
    string? DefaultSchema,
    IReadOnlyList<SqlCatalogObject> Objects,
    bool IsPartial = false,
    string? Limitation = null)
{
    /// <summary>
    /// Optional expression-callable routines reported by the connected server.
    /// Providers without a reliable routine catalog leave this empty.
    /// </summary>
    public IReadOnlyList<SqlCatalogRoutine> Routines { get; init; } = [];

    /// <summary>
    /// States whether absence from <see cref="Routines"/> is authoritative.
    /// Partial means a normally available catalog hit a query, time, or size
    /// boundary and therefore cannot safely suppress dialect-library entries.
    /// </summary>
    public SqlCatalogCoverage RoutineCoverage { get; init; } = SqlCatalogCoverage.None;

    /// <summary>
    /// Server-reported intrinsic symbols (for example SQL keywords) used only
    /// to corroborate an operator Calcite already understands.
    /// </summary>
    public IReadOnlyList<SqlCatalogIntrinsicSymbol> IntrinsicSymbols { get; init; } = [];

    public SqlCatalogCoverage IntrinsicCoverage { get; init; } = SqlCatalogCoverage.None;
}

public sealed record SqlCatalogObject(
    DatabaseObjectId Id,
    DatabaseTableKind Kind,
    IReadOnlyList<SqlCatalogColumn> Columns);

public sealed record SqlCatalogColumn(
    string Name,
    string DataTypeName,
    DatabaseValueKind ValueKind,
    bool? IsNullable);

public sealed record SqlCatalogRoutine(
    DatabaseObjectId Id,
    SqlCatalogRoutineKind Kind,
    string Signature,
    IReadOnlyList<SqlCatalogRoutineParameter> Parameters,
    string? ReturnTypeName,
    DatabaseValueKind? ReturnValueKind,
    int MinimumArgumentCount,
    int? MaximumArgumentCount);

public sealed record SqlCatalogRoutineParameter(
    string? Name,
    string DataTypeName,
    DatabaseValueKind? ValueKind,
    SqlCatalogRoutineParameterMode Mode,
    bool IsOptional = false,
    bool IsVariadic = false);

public enum SqlCatalogRoutineKind
{
    Unknown,
    Scalar,
    Aggregate,
    Window,
    Table,
}

public enum SqlCatalogRoutineParameterMode
{
    Unknown,
    In,
    Out,
    InOut,
}

public sealed record SqlCatalogIntrinsicSymbol(
    string Name,
    SqlCatalogIntrinsicKind Kind);

public enum SqlCatalogIntrinsicKind
{
    Keyword,
}

public enum SqlCatalogCoverage
{
    None,
    UserDefinedOnly,
    Complete,
    Partial,
}

/// <summary>
/// Ephemeral editor context sent with one completion request. Explicit SQL
/// scope always takes precedence over this preferred object.
/// </summary>
public sealed record SqlCompletionContext(DatabaseObjectId? PreferredObject)
{
    public static SqlCompletionContext Empty { get; } = new((DatabaseObjectId?)null);
}

public sealed record SqlCompletionResult(
    int ReplacementStart,
    int ReplacementLength,
    IReadOnlyList<SqlCompletionItem> Items)
{
    public static SqlCompletionResult Empty { get; } = new(0, 0, []);
}

public sealed record SqlCompletionItem(
    string Label,
    SqlCompletionItemKind Kind,
    string? Detail,
    string InsertText);

public enum SqlCompletionItemKind
{
    Other,
    Keyword,
    Catalog,
    Schema,
    Table,
    View,
    Column,
    Function,
    DataType,
}

public sealed record SqlDiagnostic(
    string Message,
    SqlDiagnosticSeverity Severity,
    int Start,
    int Length,
    string? Code = null);

public enum SqlDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}
