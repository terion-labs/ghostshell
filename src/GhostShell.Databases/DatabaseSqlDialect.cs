using GhostShell.Application;

namespace GhostShell.Databases;

internal enum DatabaseFamily
{
    Sqlite,
    PostgreSql,
    MySql,
    SqlServer,
    DuckDb,
    Oracle,
    Firebird,
    ClickHouse,
}

internal sealed record DatabaseSqlParameter(string Name, object? Value);

internal sealed record DatabaseSqlCommand(
    string Sql,
    IReadOnlyList<DatabaseSqlParameter> Parameters);

/// <summary>
/// The intentionally small SQL variation below the provider-neutral browser.
/// Catalog discovery remains separate because database catalogs are not a SQL
/// dialect concern.
/// </summary>
internal sealed partial class DatabaseSqlDialect
{
    // ReadTableAsync asks for one look-ahead row so it can expose HasMore
    // without a second query. The public page size remains capped at 5000.
    private const int MaximumPageSize = 5001;

    private DatabaseSqlDialect(
        DatabaseFamily family,
        bool canEdit,
        bool supportsIndexes = true,
        bool supportsPostgreSqlGeneratedColumns = false,
        bool supportsForeignKeys = true)
    {
        Family = family;
        CanEdit = canEdit;
        SupportsIndexes = supportsIndexes;
        SupportsPostgreSqlGeneratedColumns = supportsPostgreSqlGeneratedColumns;
        SupportsForeignKeys = supportsForeignKeys;
    }

    public DatabaseFamily Family { get; }

    public bool CanEdit { get; }

    public bool SupportsIndexes { get; }

    public bool SupportsPostgreSqlGeneratedColumns { get; }

    public bool SupportsForeignKeys { get; }

    public static DatabaseSqlDialect For(string driverId) => driverId switch
    {
        "sqlite" => new(DatabaseFamily.Sqlite, canEdit: true),
        "postgres" or "cockroach" => new(
            DatabaseFamily.PostgreSql,
            canEdit: true,
            supportsPostgreSqlGeneratedColumns: true),
        // Redshift exposes PostgreSQL-compatible reads but has no secondary
        // indexes and does not enforce declared keys. Without an enforced key,
        // single-row optimistic mutations cannot be made safe.
        "redshift" => new(
            DatabaseFamily.PostgreSql,
            canEdit: false,
            supportsIndexes: false,
            supportsForeignKeys: false),
        "mysql" or "mariadb" => new(DatabaseFamily.MySql, canEdit: true),
        "sqlserver" => new(DatabaseFamily.SqlServer, canEdit: true),
        "duckdb" => new(DatabaseFamily.DuckDb, canEdit: true),
        "oracle" => new(DatabaseFamily.Oracle, canEdit: true),
        "firebird" => new(DatabaseFamily.Firebird, canEdit: true),
        // ClickHouse exposes UPDATE in recent releases, but mutation cost and
        // compatibility vary by server version. Browsing stays available while
        // row editing remains conservatively disabled.
        "clickhouse" => new(
            DatabaseFamily.ClickHouse,
            canEdit: false,
            supportsForeignKeys: false),
        _ => throw new ArgumentException($"Unknown database driver '{driverId}'.", nameof(driverId)),
    };

    public string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return Family switch
        {
            DatabaseFamily.MySql or DatabaseFamily.ClickHouse =>
                $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`",
            DatabaseFamily.SqlServer =>
                $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
            _ => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
        };
    }

    public string QuoteObject(DatabaseObjectId objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        return string.Join('.', ObjectComponents(objectId).Select(QuoteIdentifier));
    }

    private IEnumerable<string> ObjectComponents(DatabaseObjectId objectId) => Family switch
    {
        DatabaseFamily.SqlServer or DatabaseFamily.DuckDb =>
            Present(objectId.Catalog, objectId.Schema, objectId.Name),
        // Discovery intentionally exposes only the main SQLite catalog.
        // Qualifying it prevents a later TEMP table with the same name
        // from redirecting a preview or mutation to the wrong object.
        DatabaseFamily.Sqlite => Present(objectId.Schema ?? "main", objectId.Name),
        DatabaseFamily.PostgreSql or DatabaseFamily.MySql or DatabaseFamily.Oracle
            or DatabaseFamily.ClickHouse =>
            Present(objectId.Schema, objectId.Name),
        _ => Present(objectId.Name),
    };

    public string ParameterMarker(string name) => Family switch
    {
        DatabaseFamily.Oracle => $":{name}",
        DatabaseFamily.DuckDb => $"${name}",
        _ => $"@{name}",
    };

    public DatabaseSqlCommand BuildSelect(
        DatabaseObjectId table,
        IReadOnlyList<DatabaseColumnSchema> columns,
        DatabaseTableQuery query)
    {
        ValidatePage(query);
        var knownColumns = columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var parameters = new List<DatabaseSqlParameter>();
        var predicates = query.Filters
            .Select(filter => BuildPredicate(filter, knownColumns, parameters))
            .ToArray();
        var projectedColumns = ProjectColumns(columns, query);
        var selected = projectedColumns.Count == 0
            ? "*"
            : string.Join(", ", projectedColumns.Select(column => QuoteIdentifier(column.Name)));
        var sql = $"SELECT {selected} FROM {QuoteObject(table)}";
        if (predicates.Length > 0)
        {
            sql += " WHERE " + string.Join(" AND ", predicates);
        }

        sql += BuildOrderBy(query.Sorts, columns, knownColumns, appendPrimaryKey: true);
        sql += BuildPageClause(query.Offset, query.Limit);
        return new DatabaseSqlCommand(sql + ';', parameters);
    }

    internal IReadOnlyList<DatabaseColumnSchema> ProjectColumns(
        IReadOnlyList<DatabaseColumnSchema> columns,
        DatabaseTableQuery query) =>
        ResolveProjectedColumns(columns, column => column.Name, query);

    public DatabaseSqlCommand BuildCount(
        DatabaseObjectId table,
        IReadOnlyList<DatabaseColumnSchema> columns,
        IReadOnlyList<DatabaseFilterCondition> filters)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(filters);
        var knownColumns = columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var parameters = new List<DatabaseSqlParameter>();
        var predicates = filters
            .Select(filter => BuildPredicate(filter, knownColumns, parameters))
            .ToArray();
        var sql = $"SELECT COUNT(*) FROM {QuoteObject(table)}";
        if (predicates.Length > 0)
        {
            sql += " WHERE " + string.Join(" AND ", predicates);
        }

        return new DatabaseSqlCommand(sql + ';', parameters);
    }

    public DatabaseSqlCommand BuildQuerySelect(
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> columns,
        DatabaseTableQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSql);
        ArgumentNullException.ThrowIfNull(columns);
        ValidatePage(query);
        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "A result query must expose at least one column.",
                nameof(columns));
        }

        var schema = columns
            .Select((column, ordinal) => new DatabaseColumnSchema(
                column.Name,
                ordinal,
                column.DataTypeName,
                column.ValueKind,
                column.ClrTypeName,
                column.IsNullable,
                IsPrimaryKey: column.IsKey,
                IsIdentity: column.IsIdentity,
                IsReadOnly: column.IsReadOnly,
                DefaultExpression: column.DefaultExpression))
            .ToArray();
        var knownColumns = schema.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var parameters = new List<DatabaseSqlParameter>();
        var predicates = query.Filters
            .Select(filter => BuildPredicate(filter, knownColumns, parameters))
            .ToArray();
        var source = sourceSql.TrimEnd();
        if (source.EndsWith(';'))
        {
            source = source[..^1].TrimEnd();
        }
        source = PrepareDerivedTableSource(source);

        var queryAlias = QuoteIdentifier("__ghostshell_query");
        var aliasJoiner = Family == DatabaseFamily.Oracle ? " " : " AS ";
        var derivedColumnAliases = Family is DatabaseFamily.Firebird or DatabaseFamily.SqlServer
            ? " (" + string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name))) + ")"
            : string.Empty;
        // DuckDB 1.2 can raise an internal optimizer error when an outer
        // ORDER BY wraps an expression query whose input already has LIMIT.
        // A materialized CTE preserves the source query boundary and avoids
        // that invalid filter pushdown. The newline also keeps a final line
        // comment in the user's SELECT from consuming generated SQL.
        var projectedColumns = ProjectColumns(columns, query);
        var selected = string.Join(", ", projectedColumns.Select(column =>
            $"{queryAlias}.{QuoteIdentifier(column.Name)}"));
        var sql = Family == DatabaseFamily.DuckDb
            ? $"WITH {queryAlias} AS MATERIALIZED (\n{source}\n)\nSELECT {selected} FROM {queryAlias}"
            : $"SELECT {selected} FROM ({source}\n){aliasJoiner}{queryAlias}{derivedColumnAliases}";
        if (predicates.Length > 0)
        {
            sql += " WHERE " + string.Join(" AND ", predicates);
        }

        sql += BuildOrderBy(
            query.Sorts,
            schema,
            knownColumns,
            appendPrimaryKey: query.Sorts.Count > 0);
        sql += BuildPageClause(query.Offset, query.Limit);
        return new DatabaseSqlCommand(sql + ';', parameters);
    }

    internal IReadOnlyList<DatabaseColumnDescriptor> ProjectColumns(
        IReadOnlyList<DatabaseColumnDescriptor> columns,
        DatabaseTableQuery query) =>
        ResolveProjectedColumns(columns, column => column.Name, query);

    private static IReadOnlyList<TColumn> ResolveProjectedColumns<TColumn>(
        IReadOnlyList<TColumn> columns,
        Func<TColumn, string> getName,
        DatabaseTableQuery query)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(query);
        var included = query.Columns ?? [];
        var excluded = query.ExcludeColumns ?? [];
        if (included.Count > 0 && excluded.Count > 0)
        {
            throw new ArgumentException(
                "A table query cannot include and exclude columns together.",
                nameof(query));
        }

        var known = columns.ToDictionary(getName, StringComparer.Ordinal);
        if (included.Count > 0)
        {
            return [.. included.Select(name => known.TryGetValue(name, out var column)
                    ? column
                    : throw new ArgumentException(
                        $"Unknown database column '{name}'.",
                        nameof(query)))];
        }

        if (excluded.Count == 0)
        {
            return columns;
        }

        var excludedNames = excluded.ToHashSet(StringComparer.Ordinal);
        if (excludedNames.Any(name => !known.ContainsKey(name)))
        {
            throw new ArgumentException(
                "A table query excludes an unknown database column.",
                nameof(query));
        }

        var projected = columns.Where(column => !excludedNames.Contains(getName(column))).ToArray();
        if (projected.Length == 0)
        {
            throw new ArgumentException(
                "A table query must return at least one column.",
                nameof(query));
        }

        return projected;
    }

    public DatabaseSqlCommand BuildQueryCount(
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> columns,
        IReadOnlyList<DatabaseFilterCondition> filters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSql);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(filters);
        if (columns.Count == 0)
        {
            throw new ArgumentException(
                "A result query must expose at least one column.",
                nameof(columns));
        }

        var schema = columns
            .Select((column, ordinal) => new DatabaseColumnSchema(
                column.Name,
                ordinal,
                column.DataTypeName,
                column.ValueKind,
                column.ClrTypeName,
                column.IsNullable,
                IsPrimaryKey: column.IsKey,
                IsIdentity: column.IsIdentity,
                IsReadOnly: column.IsReadOnly,
                DefaultExpression: column.DefaultExpression))
            .ToArray();
        var knownColumns = schema.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var parameters = new List<DatabaseSqlParameter>();
        var predicates = filters
            .Select(filter => BuildPredicate(filter, knownColumns, parameters))
            .ToArray();
        var source = sourceSql.TrimEnd();
        if (source.EndsWith(';'))
        {
            source = source[..^1].TrimEnd();
        }
        source = PrepareDerivedTableSource(source);

        var queryAlias = QuoteIdentifier("__ghostshell_query");
        var aliasJoiner = Family == DatabaseFamily.Oracle ? " " : " AS ";
        var derivedColumnAliases = Family is DatabaseFamily.Firebird or DatabaseFamily.SqlServer
            ? " (" + string.Join(", ", columns.Select(column => QuoteIdentifier(column.Name))) + ")"
            : string.Empty;
        var sql = Family == DatabaseFamily.DuckDb
            ? $"WITH {queryAlias} AS MATERIALIZED (\n{source}\n)\nSELECT COUNT(*) FROM {queryAlias}"
            : $"SELECT COUNT(*) FROM ({source}\n){aliasJoiner}{queryAlias}{derivedColumnAliases}";
        if (predicates.Length > 0)
        {
            sql += " WHERE " + string.Join(" AND ", predicates);
        }

        return new DatabaseSqlCommand(sql + ';', parameters);
    }

    private string PrepareDerivedTableSource(string source)
    {
        if (Family != DatabaseFamily.SqlServer)
        {
            return source;
        }

        var tokens = ReadTopLevelSqlTokens(source);
        var hasOrderBy = tokens
            .Zip(tokens.Skip(1), (left, right) => (left, right))
            .Any(pair => string.Equals(pair.left, "ORDER", StringComparison.Ordinal) && string.Equals(pair.right, "BY", StringComparison.Ordinal));
        var orderIsAlreadyLegal = tokens.Contains("OFFSET", StringComparer.Ordinal)
            || tokens.Contains("TOP", StringComparer.Ordinal)
            || tokens
                .Zip(tokens.Skip(1), (left, right) => (left, right))
                .Any(pair => string.Equals(pair.left, "FOR", StringComparison.Ordinal) && pair.right is "XML" or "JSON");
        return hasOrderBy && !orderIsAlreadyLegal
            ? source + "\nOFFSET 0 ROWS"
            : source;
    }

    private static IReadOnlyList<string> ReadTopLevelSqlTokens(string sql)
    {
        var tokens = new List<string>();
        var depth = 0;
        for (var index = 0; index < sql.Length;)
        {
            var character = sql[index];
            if (character == '\'' || character == '"' || character == '[')
            {
                var terminator = character == '[' ? ']' : character;
                index++;
                while (index < sql.Length)
                {
                    if (sql[index] != terminator)
                    {
                        index++;
                        continue;
                    }

                    if (index + 1 < sql.Length && sql[index + 1] == terminator)
                    {
                        index += 2;
                        continue;
                    }

                    index++;
                    break;
                }
                continue;
            }

            if (character == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
            {
                index += 2;
                while (index < sql.Length && sql[index] is not ('\r' or '\n'))
                {
                    index++;
                }
                continue;
            }

            if (character == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
            {
                index += 2;
                while (index + 1 < sql.Length
                    && !(sql[index] == '*' && sql[index + 1] == '/'))
                {
                    index++;
                }
                index = Math.Min(sql.Length, index + 2);
                continue;
            }

            if (character == '(')
            {
                depth++;
                index++;
                continue;
            }

            if (character == ')')
            {
                depth = Math.Max(0, depth - 1);
                index++;
                continue;
            }

            if (depth == 0 && (char.IsLetter(character) || character == '_'))
            {
                var start = index++;
                while (index < sql.Length
                    && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
                {
                    index++;
                }
                tokens.Add(sql[start..index].ToUpperInvariant());
                continue;
            }

            index++;
        }

        return tokens;
    }

    public DatabaseSqlCommand BuildInsert(
        DatabaseObjectId table,
        DatabaseObjectDetails details,
        DatabaseInsertedRow row)
    {
        var values = ValidateEdits(row.Values, details, allowDefault: true)
            .Where(value => value.State != DatabaseEditValueState.Default)
            .ToArray();
        if (values.Length == 0)
        {
            var sql = Family switch
            {
                DatabaseFamily.MySql => $"INSERT INTO {QuoteObject(table)} () VALUES ();",
                // Oracle has no standalone DEFAULT VALUES form. Naming one
                // insertable column and assigning DEFAULT asks Oracle to apply
                // the same row defaults while leaving identities out of the list.
                DatabaseFamily.Oracle => BuildOracleDefaultInsert(table, details),
                _ => $"INSERT INTO {QuoteObject(table)} DEFAULT VALUES;",
            };
            return new DatabaseSqlCommand(sql, []);
        }

        var parameters = new List<DatabaseSqlParameter>(values.Length);
        var markers = new string[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var name = $"v{index}";
            if (values[index].State == DatabaseEditValueState.Null)
            {
                markers[index] = "NULL";
            }
            else
            {
                markers[index] = AddMutationParameter(
                    name,
                    values[index],
                    details,
                    parameters);
            }
        }

        var names = string.Join(", ", values.Select(value => QuoteIdentifier(value.ColumnName)));
        return new DatabaseSqlCommand(
            $"INSERT INTO {QuoteObject(table)} ({names}) VALUES ({string.Join(", ", markers)});",
            parameters);
    }

    public string BuildInsertStatement(
        DatabaseObjectId table,
        DatabaseObjectDetails details,
        DatabaseInsertedRow row,
        int maximumUtf8Bytes = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(row);

        var values = ValidateEdits(row.Values, details, allowDefault: true)
            .Where(value => value.State != DatabaseEditValueState.Default)
            .ToArray();
        EnsureInsertFits(table, details, values, maximumUtf8Bytes);
        if (values.Length == 0)
        {
            return Family switch
            {
                DatabaseFamily.MySql => $"INSERT INTO {QuoteObject(table)} () VALUES ();",
                DatabaseFamily.Oracle => BuildOracleDefaultInsert(table, details),
                _ => $"INSERT INTO {QuoteObject(table)} DEFAULT VALUES;",
            };
        }

        var columns = details.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var names = string.Join(", ", values.Select(value => QuoteIdentifier(value.ColumnName)));
        var literals = values.Select(value => value.State == DatabaseEditValueState.Null
            ? "NULL"
            : FormatInsertLiteral(value.Value!, columns[value.ColumnName]));
        return $"INSERT INTO {QuoteObject(table)} ({names}) VALUES ({string.Join(", ", literals)});";
    }

    public DatabaseSqlCommand BuildUpdate(
        DatabaseObjectId table,
        DatabaseObjectDetails details,
        DatabaseUpdatedRow row)
    {
        var changes = ValidateEdits(row.Changes, details, allowDefault: false);
        if (changes.Count == 0)
        {
            throw new ArgumentException("An update must contain at least one changed value.", nameof(row));
        }

        var parameters = new List<DatabaseSqlParameter>();
        var assignments = new List<string>(changes.Count);
        foreach (var change in changes)
        {
            var value = change.State == DatabaseEditValueState.Null
                ? "NULL"
                : AddMutationParameter(
                    $"p{parameters.Count}",
                    change,
                    details,
                    parameters);
            assignments.Add($"{QuoteIdentifier(change.ColumnName)} = {value}");
        }

        var predicate = BuildConcurrencyPredicate(row.Keys, row.OriginalValues, details, parameters);
        return new DatabaseSqlCommand(
            $"UPDATE {QuoteObject(table)} SET {string.Join(", ", assignments)} WHERE {predicate};",
            parameters);
    }

    public DatabaseSqlCommand BuildDelete(
        DatabaseObjectId table,
        DatabaseObjectDetails details,
        DatabaseDeletedRow row)
    {
        var parameters = new List<DatabaseSqlParameter>();
        var predicate = BuildConcurrencyPredicate(row.Keys, row.OriginalValues, details, parameters);
        return new DatabaseSqlCommand(
            $"DELETE FROM {QuoteObject(table)} WHERE {predicate};",
            parameters);
    }

    private static string[] Present(params string?[] components) => [.. components
        .Where(component => !string.IsNullOrWhiteSpace(component))
        .Select(component => component!)];

    private static void ValidatePage(DatabaseTableQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MaximumPageSize);
    }

    private string BuildPredicate(
        DatabaseFilterCondition filter,
        IReadOnlyDictionary<string, DatabaseColumnSchema> columns,
        ICollection<DatabaseSqlParameter> parameters)
    {
        if (!columns.TryGetValue(filter.ColumnName, out var column))
        {
            throw new ArgumentException($"Unknown filter column '{filter.ColumnName}'.", nameof(filter));
        }

        var identifier = QuoteIdentifier(column.Name);
        if (filter.Operator == DatabaseFilterOperator.IsNull)
        {
            return $"{identifier} IS NULL";
        }

        if (filter.Operator == DatabaseFilterOperator.IsNotNull)
        {
            return $"{identifier} IS NOT NULL";
        }

        if (filter.Value is null)
        {
            throw new ArgumentException("This filter operator requires a value.", nameof(filter));
        }

        if (filter.Operator is DatabaseFilterOperator.In or DatabaseFilterOperator.NotIn)
        {
            if (filter.Value is not System.Collections.IEnumerable values
                || filter.Value is string
                || filter.Value is byte[])
            {
                throw new ArgumentException(
                    "IN filters require a collection of typed values.",
                    nameof(filter));
            }

            var filterValues = values.Cast<object?>().ToArray();
            if (filterValues.Length is 0 or > 500)
            {
                throw new ArgumentException(
                    "IN filters require between 1 and 500 values.",
                    nameof(filter));
            }

            var markers = filterValues
                .Select(value => value is null
                    ? throw new ArgumentException(
                        "IN filter collections cannot contain NULL. Use IS NULL explicitly.",
                        nameof(filter))
                    : AddParameter(value, parameters))
                .ToArray();
            var listOperation = filter.Operator == DatabaseFilterOperator.In ? "IN" : "NOT IN";
            return $"{identifier} {listOperation} ({string.Join(", ", markers)})";
        }

        var isTextMatch = filter.Operator is DatabaseFilterOperator.Contains
            or DatabaseFilterOperator.NotContains
            or DatabaseFilterOperator.StartsWith
            or DatabaseFilterOperator.EndsWith;
        if (isTextMatch && Family == DatabaseFamily.ClickHouse)
        {
            var textMarker = AddParameter(
                Convert.ToString(
                    filter.Value,
                    System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                parameters);
            return filter.Operator switch
            {
                DatabaseFilterOperator.Contains => $"position({identifier}, {textMarker}) > 0",
                DatabaseFilterOperator.NotContains => $"position({identifier}, {textMarker}) = 0",
                DatabaseFilterOperator.StartsWith => $"startsWith({identifier}, {textMarker})",
                DatabaseFilterOperator.EndsWith => $"endsWith({identifier}, {textMarker})",
                _ => throw new ArgumentOutOfRangeException(nameof(filter)),
            };
        }

        var text = isTextMatch
            ? EscapeLikePattern(Convert.ToString(
                filter.Value,
                System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)
            : null;
        var value = filter.Operator switch
        {
            DatabaseFilterOperator.Contains => $"%{text}%",
            DatabaseFilterOperator.NotContains => $"%{text}%",
            DatabaseFilterOperator.StartsWith => $"{text}%",
            DatabaseFilterOperator.EndsWith => $"%{text}",
            _ => filter.Value,
        };
        var marker = AddParameter(value, parameters);
        var operation = filter.Operator switch
        {
            DatabaseFilterOperator.Equal => "=",
            DatabaseFilterOperator.NotEqual => "<>",
            DatabaseFilterOperator.LessThan => "<",
            DatabaseFilterOperator.LessThanOrEqual => "<=",
            DatabaseFilterOperator.GreaterThan => ">",
            DatabaseFilterOperator.GreaterThanOrEqual => ">=",
            DatabaseFilterOperator.Contains or DatabaseFilterOperator.NotContains
                or DatabaseFilterOperator.StartsWith
                or DatabaseFilterOperator.EndsWith => "LIKE",
            _ => throw new ArgumentOutOfRangeException(nameof(filter)),
        };
        var escapeClause = isTextMatch
            ? " ESCAPE '!'"
            : string.Empty;
        var negation = filter.Operator == DatabaseFilterOperator.NotContains ? "NOT " : string.Empty;
        return $"{identifier} {negation}{operation} {marker}{escapeClause}";
    }

    private static string EscapeLikePattern(string value) => value
            .Replace("!", "!!", StringComparison.Ordinal)
            .Replace("%", "!%", StringComparison.Ordinal)
            .Replace("_", "!_", StringComparison.Ordinal);

    private string BuildOrderBy(
        IReadOnlyList<DatabaseSort> requested,
        IReadOnlyList<DatabaseColumnSchema> columns,
        IReadOnlyDictionary<string, DatabaseColumnSchema> knownColumns,
        bool appendPrimaryKey)
    {
        var sorts = requested.ToList();
        if (appendPrimaryKey)
        {
            foreach (var key in columns
                         .Where(column => column.IsPrimaryKey)
                         .OrderBy(column => column.PrimaryKeyOrdinal ?? int.MaxValue))
            {
                if (sorts.All(sort => !string.Equals(
                        sort.ColumnName,
                        key.Name,
                        StringComparison.Ordinal)))
                {
                    // User sorting remains primary, while the complete key provides
                    // a deterministic tie-breaker for offset paging.
                    sorts.Add(new DatabaseSort(key.Name));
                }
            }
        }
        if (sorts.Count == 0)
        {
            return Family switch
            {
                DatabaseFamily.SqlServer => " ORDER BY (SELECT NULL)",
                DatabaseFamily.Oracle => " ORDER BY 1",
                _ => string.Empty,
            };
        }

        var expressions = new List<string>(sorts.Count);
        foreach (var sort in sorts)
        {
            if (!knownColumns.TryGetValue(sort.ColumnName, out var column))
            {
                throw new ArgumentException($"Unknown sort column '{sort.ColumnName}'.", nameof(requested));
            }

            expressions.Add($"{QuoteIdentifier(column.Name)}{(sort.Descending ? " DESC" : " ASC")}");
        }

        return " ORDER BY " + string.Join(", ", expressions);
    }

    private string BuildPageClause(int offset, int limit) => Family switch
    {
        DatabaseFamily.SqlServer or DatabaseFamily.Oracle =>
            $" OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY",
        DatabaseFamily.Firebird => $" ROWS {(long)offset + 1} TO {(long)offset + limit}",
        _ => $" LIMIT {limit} OFFSET {offset}",
    };

    private IReadOnlyList<DatabaseColumnEdit> ValidateEdits(
        IReadOnlyList<DatabaseColumnEdit> edits,
        DatabaseObjectDetails details,
        bool allowDefault)
    {
        var columns = details.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            if (!columns.TryGetValue(edit.ColumnName, out var column))
            {
                throw new ArgumentException($"Unknown edited column '{edit.ColumnName}'.", nameof(edits));
            }

            if (!column.CanEdit)
            {
                throw new ArgumentException($"Column '{column.Name}' is generated or read-only.", nameof(edits));
            }

            if (!allowDefault && edit.State == DatabaseEditValueState.Default)
            {
                throw new ArgumentException("DEFAULT is only valid for a newly inserted row.", nameof(edits));
            }

            if (edit.State == DatabaseEditValueState.Null && column.IsNullable == false)
            {
                throw new ArgumentException($"Column '{column.Name}' does not allow NULL.", nameof(edits));
            }

            if (edit.State == DatabaseEditValueState.Value && edit.Value is null)
            {
                throw new ArgumentException($"Column '{column.Name}' has no value.", nameof(edits));
            }
        }

        return edits;
    }

    private string BuildConcurrencyPredicate(
        IReadOnlyList<DatabaseColumnEdit> keys,
        IReadOnlyList<DatabaseColumnEdit> originals,
        DatabaseObjectDetails details,
        ICollection<DatabaseSqlParameter> parameters)
    {
        if (keys.Count == 0 || details.PrimaryKey.Count == 0)
        {
            throw new ArgumentException("A keyed table is required for row editing.", nameof(keys));
        }

        var requiredKeys = details.PrimaryKey
            .Select(column => column.Name)
            .ToHashSet(StringComparer.Ordinal);
        var suppliedKeys = keys
            .Select(key => key.ColumnName)
            .ToHashSet(StringComparer.Ordinal);
        if (keys.Count != requiredKeys.Count || !suppliedKeys.SetEquals(requiredKeys))
        {
            throw new ArgumentException(
                "Every primary-key column must have exactly one original value.",
                nameof(keys));
        }

        if (keys.Any(key => key.State == DatabaseEditValueState.Default
                || key.State == DatabaseEditValueState.Value && key.Value is null)
            || originals.Any(value => value.State == DatabaseEditValueState.Default
                || value.State == DatabaseEditValueState.Value && value.Value is null))
        {
            throw new ArgumentException(
                "Concurrency values must distinguish a typed value from NULL.",
                nameof(keys));
        }

        var allowed = details.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var terms = new List<string>(keys.Count + originals.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in keys.Select(value => (Value: value, IsKey: true))
                     .Concat(originals.Select(value => (Value: value, IsKey: false))))
        {
            var value = entry.Value;
            if (!allowed.TryGetValue(value.ColumnName, out var column))
            {
                throw new ArgumentException($"Unknown concurrency column '{value.ColumnName}'.");
            }

            if (!entry.IsKey && !SupportsConcurrencyEquality(column))
            {
                continue;
            }

            if (!seen.Add(column.Name))
            {
                continue;
            }

            terms.Add(value.State == DatabaseEditValueState.Null
                ? $"{QuoteIdentifier(column.Name)} IS NULL"
                : $"{QuoteIdentifier(column.Name)} = {AddParameter(value.Value, parameters)}");
        }

        return string.Join(" AND ", terms);
    }

    private string BuildOracleDefaultInsert(
        DatabaseObjectId table,
        DatabaseObjectDetails details)
    {
        var column = OracleDefaultColumn(details);
        return $"INSERT INTO {QuoteObject(table)} ({QuoteIdentifier(column.Name)}) VALUES (DEFAULT);";
    }

    private static DatabaseColumnSchema OracleDefaultColumn(DatabaseObjectDetails details) =>
        details.Columns.FirstOrDefault(candidate =>
                candidate.CanEdit
                && (candidate.DefaultExpression is not null || candidate.IsNullable == true))
            ?? details.Columns.FirstOrDefault(candidate =>
                candidate.IsIdentity && !candidate.IsGenerated)
            ?? throw new InvalidOperationException(
                "Oracle requires a default-bearing, nullable, or identity column for a default-valued row.");

    private string FormatInsertLiteral(object value, DatabaseColumnSchema column)
    {
        if (value is byte[] bytes)
        {
            return FormatBinaryLiteral(bytes);
        }

        if (value is ReadOnlyMemory<byte> readOnlyBytes)
        {
            return FormatBinaryLiteral(readOnlyBytes.Span);
        }

        if (value is Memory<byte> writableBytes)
        {
            return FormatBinaryLiteral(writableBytes.Span);
        }

        if (value is bool boolean)
        {
            return Family is DatabaseFamily.Sqlite or DatabaseFamily.SqlServer
                ? boolean ? "1" : "0"
                : boolean ? "TRUE" : "FALSE";
        }

        if (value is System.Text.Json.JsonElement json)
        {
            return FormatTextLiteral(json.GetRawText(), column);
        }

        if (value is string text)
        {
            return FormatTextLiteral(text, column);
        }

        if (value is char character)
        {
            return FormatTextLiteral(character.ToString(), column);
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            var formatted = dateTimeOffset.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
                System.Globalization.CultureInfo.InvariantCulture);
            return Family == DatabaseFamily.Oracle
                ? $"TO_TIMESTAMP_TZ({QuoteStandardString(formatted)}, 'YYYY-MM-DD\"T\"HH24:MI:SS.FFTZH:TZM')"
                : FormatTextLiteral(formatted, column);
        }

        if (value is DateTime dateTime)
        {
            var formatted = dateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff",
                System.Globalization.CultureInfo.InvariantCulture);
            return Family == DatabaseFamily.Oracle
                ? $"TO_TIMESTAMP({QuoteStandardString(formatted)}, 'YYYY-MM-DD\"T\"HH24:MI:SS.FF')"
                : FormatTextLiteral(formatted, column);
        }

        if (value is DateOnly date)
        {
            var formatted = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            return Family == DatabaseFamily.Oracle
                ? $"DATE {QuoteStandardString(formatted)}"
                : FormatTextLiteral(formatted, column);
        }

        if (value is TimeOnly time)
        {
            return FormatTextLiteral(
                time.ToString("HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture),
                column);
        }

        if (value is TimeSpan duration)
        {
            return FormatTextLiteral(duration.ToString("c", System.Globalization.CultureInfo.InvariantCulture), column);
        }

        if (value is Guid guid)
        {
            return FormatTextLiteral(guid.ToString("D", System.Globalization.CultureInfo.InvariantCulture), column);
        }

        if (value is sbyte or byte or short or ushort or int or uint or long or ulong
            or decimal or float or double)
        {
            var number = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException($"Could not format column '{column.Name}'.");
            if (number is "NaN" or "Infinity" or "-Infinity")
            {
                throw new InvalidOperationException(
                    $"Column '{column.Name}' contains a non-finite number that cannot be exported safely.");
            }

            return number;
        }

        throw new InvalidOperationException(
            $"Column '{column.Name}' uses a value that cannot be represented safely in an INSERT script.");
    }

    private string FormatTextLiteral(string value, DatabaseColumnSchema column)
    {
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Column '{column.Name}' contains a NUL character that cannot be represented safely.");
        }

        var expression = Family switch
        {
            DatabaseFamily.PostgreSql =>
                $"convert_from(decode('{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(value))}', 'hex'), 'UTF8')",
            DatabaseFamily.MySql =>
                $"CONVERT(X'{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(value))}' USING utf8mb4)",
            DatabaseFamily.SqlServer => $"N{QuoteStandardString(value)}",
            DatabaseFamily.ClickHouse =>
                $"unhex('{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(value))}')",
            _ => QuoteStandardString(value),
        };

        if (Family != DatabaseFamily.PostgreSql)
        {
            return expression;
        }

        var castType = column.DataTypeName.Trim().ToLowerInvariant() switch
        {
            "json" => "json",
            "jsonb" => "jsonb",
            "xml" => "xml",
            _ => null,
        };
        return castType is null ? expression : $"CAST({expression} AS {castType})";
    }

    private string FormatBinaryLiteral(ReadOnlySpan<byte> value)
    {
        var hex = Convert.ToHexString(value);
        return Family switch
        {
            DatabaseFamily.PostgreSql => $"decode('{hex}', 'hex')",
            DatabaseFamily.SqlServer => $"0x{hex}",
            DatabaseFamily.DuckDb => $"from_hex('{hex}')",
            DatabaseFamily.Oracle => $"HEXTORAW('{hex}')",
            DatabaseFamily.ClickHouse => $"unhex('{hex}')",
            _ => $"X'{hex}'",
        };
    }

    private static string QuoteStandardString(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private bool SupportsConcurrencyEquality(DatabaseColumnSchema column)
    {
        if (column.ValueKind is DatabaseValueKind.Other
            or DatabaseValueKind.Binary
            or DatabaseValueKind.Json
            or DatabaseValueKind.Collection)
        {
            return false;
        }

        var typeName = column.DataTypeName.Trim().ToLowerInvariant();
        return Family switch
        {
            DatabaseFamily.PostgreSql => typeName is not ("json" or "xml"),
            DatabaseFamily.SqlServer => typeName is not ("text" or "ntext" or "xml"),
            DatabaseFamily.Oracle => typeName is not ("clob" or "nclob" or "bfile" or "long" or "xmltype"),
            // The metadata reader labels a text-subtype BLOB as TEXT. Neither
            // Firebird BLOB form is a portable equality operand.
            DatabaseFamily.Firebird => typeName is not ("blob" or "text"),
            _ => true,
        };
    }

    private string AddParameter(object? value, ICollection<DatabaseSqlParameter> parameters)
    {
        var name = $"p{parameters.Count}";
        parameters.Add(new DatabaseSqlParameter(name, value));
        return ParameterMarker(name);
    }

    private string AddMutationParameter(
        string name,
        DatabaseColumnEdit edit,
        DatabaseObjectDetails details,
        ICollection<DatabaseSqlParameter> parameters)
    {
        parameters.Add(new DatabaseSqlParameter(name, edit.Value));
        var marker = ParameterMarker(name);
        if (Family != DatabaseFamily.PostgreSql || edit.Value is not string)
        {
            return marker;
        }

        var column = details.Columns.Single(candidate =>
            string.Equals(candidate.Name, edit.ColumnName, StringComparison.Ordinal));
        var castType = column.DataTypeName.Trim().ToLowerInvariant() switch
        {
            "json" => "json",
            "jsonb" => "jsonb",
            "xml" => "xml",
            _ => null,
        };
        return castType is null
            ? marker
            : $"CAST({marker} AS {castType})";
    }
}
