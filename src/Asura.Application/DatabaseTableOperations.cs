namespace Asura.Application;

public enum DatabaseFilterOperator
{
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    In,
    NotIn,
    IsNull,
    IsNotNull,
}

public sealed record DatabaseFilterCondition(
    string ColumnName,
    DatabaseFilterOperator Operator,
    object? Value = null);

public sealed record DatabaseSort(string ColumnName, bool Descending = false);

public sealed record DatabaseTableQuery(
    IReadOnlyList<DatabaseFilterCondition> Filters,
    IReadOnlyList<DatabaseSort> Sorts,
    int Offset,
    int Limit,
    IReadOnlyList<string>? Columns = null,
    IReadOnlyList<string>? ExcludeColumns = null)
{
    public static DatabaseTableQuery FirstPage(int limit) => new([], [], 0, limit);
}

public sealed record DatabaseTablePage(
    DatabaseQueryPage Result,
    int Offset,
    int Limit,
    bool HasMore,
    long TotalRows = 0,
    long? TableRows = null);

public enum DatabaseEditValueState
{
    Default,
    Null,
    Value,
}

public sealed record DatabaseColumnEdit(
    string ColumnName,
    DatabaseEditValueState State,
    object? Value = null);

public sealed record DatabaseInsertedRow(IReadOnlyList<DatabaseColumnEdit> Values);

public sealed record DatabaseUpdatedRow(
    IReadOnlyList<DatabaseColumnEdit> Keys,
    IReadOnlyList<DatabaseColumnEdit> Changes,
    IReadOnlyList<DatabaseColumnEdit> OriginalValues);

public sealed record DatabaseDeletedRow(
    IReadOnlyList<DatabaseColumnEdit> Keys,
    IReadOnlyList<DatabaseColumnEdit> OriginalValues);

public sealed record DatabaseTableChanges(
    IReadOnlyList<DatabaseInsertedRow> Inserts,
    IReadOnlyList<DatabaseUpdatedRow> Updates,
    IReadOnlyList<DatabaseDeletedRow> Deletes)
{
    public bool IsEmpty => Inserts.Count == 0 && Updates.Count == 0 && Deletes.Count == 0;
}

public sealed record DatabaseMutationResult(
    int Inserted,
    int Updated,
    int Deleted,
    bool HasConflict = false,
    string? Message = null)
{
    public int TotalAffected => Inserted + Updated + Deleted;
}
