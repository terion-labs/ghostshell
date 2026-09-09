using System.Data.Common;
using Asura.Application;
using Oracle.ManagedDataAccess.Client;

namespace Asura.Databases;

/// <summary>
/// Maps each database family's catalog into one stable structure/index model.
/// Catalog SQL is the only intentionally provider-specific part of browsing.
/// </summary>
internal sealed class DatabaseMetadataReader(DatabaseSqlDialect dialect)
{
    /// <summary>
    /// Reads only the column facts needed by language tooling. Keeping this
    /// separate from <see cref="ReadAsync"/> avoids index and storage-engine
    /// probes while a database-wide completion catalog is assembled.
    /// </summary>
    public async Task<IReadOnlyList<DatabaseColumnSchema>> ReadColumnsOnlyAsync(
        DbConnection connection,
        DatabaseObjectId objectId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(objectId);
        return await ReadColumnsAsync(connection, objectId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DatabaseSchemaTable> ReadSchemaTableAsync(
        DbConnection connection,
        DatabaseTableDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(descriptor);
        var columns = await ReadColumnsAsync(connection, descriptor.Id, cancellationToken)
            .ConfigureAwait(false);
        var foreignKeys = await ReadForeignKeysAsync(connection, descriptor.Id, cancellationToken)
            .ConfigureAwait(false);
        return new DatabaseSchemaTable(descriptor, columns, foreignKeys);
    }

    public async Task<DatabaseObjectDetails> ReadAsync(
        DbConnection connection,
        DatabaseTableDescriptor descriptor,
        CancellationToken cancellationToken,
        bool includeIndexes = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(descriptor);

        var columns = await ReadColumnsAsync(connection, descriptor.Id, cancellationToken)
            .ConfigureAwait(false);
        var indexes = includeIndexes
            ? await ReadIndexesAsync(connection, descriptor.Id, cancellationToken).ConfigureAwait(false)
            : [];
        var hasKey = columns.Any(column => column.IsPrimaryKey);
        var hasSafelyParameterizableKey = !columns.Any(column =>
            column.IsPrimaryKey
            && column.ValueKind is (DatabaseValueKind.Other
                or DatabaseValueKind.Collection
                or DatabaseValueKind.Json));
        var requiresEngineCheck = descriptor.Kind == DatabaseTableKind.Table
            && dialect.Family == DatabaseFamily.MySql
            && dialect.CanEdit
            && hasKey;
        var supportsSafeMutations = !requiresEngineCheck
            || await SupportsSafeMutationsAsync(connection, descriptor.Id, cancellationToken)
                .ConfigureAwait(false);
        var canEdit = descriptor.Kind == DatabaseTableKind.Table
            && dialect.CanEdit
            && hasKey
            && hasSafelyParameterizableKey
            && supportsSafeMutations;
        var reason = canEdit
            ? null
            : descriptor.Kind == DatabaseTableKind.View
                ? "Views are read-only."
                : !dialect.CanEdit
                    ? "This database does not advertise safe row editing."
                    : !hasKey
                        ? "A primary key is required for safe row editing."
                        : !hasSafelyParameterizableKey
                            ? "This primary-key type cannot be parameterized safely."
                            : "Only transactional InnoDB tables support safe row editing.";
        return new DatabaseObjectDetails(descriptor, columns, indexes, canEdit, reason);
    }

    private async Task<IReadOnlyList<DatabaseColumnSchema>> ReadColumnsAsync(
        DbConnection connection,
        DatabaseObjectId objectId,
        CancellationToken cancellationToken)
    {
        var generatedColumnNames = dialect.Family == DatabaseFamily.DuckDb
            ? await ReadDuckDbGeneratedColumnNamesAsync(connection, objectId, cancellationToken)
                .ConfigureAwait(false)
            : new HashSet<string>(StringComparer.Ordinal);
        var commandText = BuildColumnsSql(objectId);
        await using var command = CreateCommand(connection, commandText, objectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var columns = new List<DatabaseColumnSchema>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = ReadString(reader, 0) ?? string.Empty;
            var typeName = ReadString(reader, 1) ?? string.Empty;
            var identity = ReadBoolean(reader, 8);
            var generated = ReadBoolean(reader, 9) || generatedColumnNames.Contains(name);
            columns.Add(new DatabaseColumnSchema(
                name,
                ReadInt32(reader, 2) ?? columns.Count + 1,
                typeName,
                DatabaseValueClassifier.Classify(null, typeName),
                IsNullable: ReadNullableBoolean(reader, 3),
                IsPrimaryKey: ReadBoolean(reader, 11),
                PrimaryKeyOrdinal: ReadInt32(reader, 12),
                IsIdentity: identity,
                IsGenerated: generated,
                IsReadOnly: ReadBoolean(reader, 10) || identity || generated,
                DefaultExpression: ReadString(reader, 4),
                Length: ReadInt64(reader, 5),
                Precision: ReadInt32(reader, 6),
                Scale: ReadInt32(reader, 7)));
        }

        return columns;
    }

    private async Task<IReadOnlySet<string>> ReadDuckDbGeneratedColumnNamesAsync(
        DbConnection connection,
        DatabaseObjectId objectId,
        CancellationToken cancellationToken)
    {
        const string commandText = """
            SELECT sql
            FROM duckdb_tables()
            WHERE database_name = $catalog AND schema_name = $schema AND table_name = $table;
            """;
        await using var command = CreateCommand(connection, commandText, objectId);
        var definition = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return DuckDbTableDefinition.FindGeneratedColumns(
            Convert.ToString(definition, System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<IReadOnlyList<DatabaseIndexSchema>> ReadIndexesAsync(
        DbConnection connection,
        DatabaseObjectId objectId,
        CancellationToken cancellationToken)
    {
        if (!dialect.SupportsIndexes)
        {
            return [];
        }

        var commandText = BuildIndexesSql(objectId);
        if (commandText.Length == 0)
        {
            return [];
        }

        await using var command = CreateCommand(connection, commandText, objectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<IndexRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new IndexRow(
                ReadString(reader, 0) ?? string.Empty,
                ReadString(reader, 1) ?? "index",
                ReadBoolean(reader, 2),
                ReadBoolean(reader, 3),
                ReadNullableBoolean(reader, 4) != false,
                ReadInt32(reader, 5) ?? rows.Count + 1,
                ReadString(reader, 6),
                ReadBoolean(reader, 7),
                ReadBoolean(reader, 8),
                ReadString(reader, 9),
                ReadString(reader, 10),
                ReadString(reader, 11)));
        }

        return [.. rows
            .GroupBy(row => row.Name, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var definition = group.Select(row => row.Definition).FirstOrDefault(value => value is not null);
                IReadOnlyDictionary<string, string>? details = definition is null
                    ? null
                    : new Dictionary<string, string>(StringComparer.Ordinal) { ["Definition"] = definition };
                return new DatabaseIndexSchema(
                    first.Name,
                    first.Kind,
                    first.IsUnique,
                    first.IsPrimary,
                    first.IsValid,
                    [.. group
                        .OrderBy(row => row.Ordinal)
                        .Select(row => new DatabaseIndexColumn(
                            row.ColumnName,
                            row.Ordinal,
                            row.IsDescending,
                            row.IsIncluded,
                            row.Expression))],
                    first.Predicate,
                    details);
            })];
    }

    private async Task<IReadOnlyList<DatabaseForeignKeySchema>> ReadForeignKeysAsync(
        DbConnection connection,
        DatabaseObjectId objectId,
        CancellationToken cancellationToken)
    {
        if (!dialect.SupportsForeignKeys)
        {
            return [];
        }

        var commandText = BuildForeignKeysSql(objectId);
        if (commandText.Length == 0)
        {
            return [];
        }

        await using var command = CreateCommand(connection, commandText, objectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = new List<ForeignKeyRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ForeignKeyRow(
                ReadString(reader, 0) ?? string.Empty,
                ReadString(reader, 1) ?? string.Empty,
                ReadString(reader, 2),
                ReadString(reader, 3),
                ReadString(reader, 4) ?? string.Empty,
                ReadString(reader, 5) ?? string.Empty,
                ReadInt32(reader, 6) ?? rows.Count + 1));
        }

        return [.. rows
            .GroupBy(row => row.Name, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new DatabaseForeignKeySchema(
                    first.Name,
                    new DatabaseObjectId(
                        first.ReferencedCatalog,
                        first.ReferencedSchema,
                        first.ReferencedTable),
                    [.. group
                        .OrderBy(row => row.Ordinal)
                        .Select(row => new DatabaseForeignKeyColumn(
                            row.ColumnName,
                            row.ReferencedColumnName,
                            row.Ordinal))]);
            })];
    }

    private DbCommand CreateCommand(
        DbConnection connection,
        string commandText,
        DatabaseObjectId objectId)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        if (command is OracleCommand oracleCommand)
        {
            // Oracle exposes catalog defaults as LONG. ODP.NET otherwise
            // fetches an empty value for that field, which makes DEFAULT
            // inserts and the structure view report the column incorrectly.
            oracleCommand.InitialLONGFetchSize = -1;
        }

        AddParameterIfReferenced(command, commandText, "catalog", objectId.Catalog);
        AddParameterIfReferenced(command, commandText, "schema", objectId.Schema);
        AddParameterIfReferenced(command, commandText, "table", objectId.Name);
        AddParameterIfReferenced(command, commandText, "object_name", objectId.Name);
        return command;
    }

    private void AddParameterIfReferenced(
        DbCommand command,
        string commandText,
        string name,
        object? value)
    {
        if (commandText.Contains(dialect.ParameterMarker(name), StringComparison.Ordinal))
        {
            AddParameter(command, name, value);
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private async Task<bool> SupportsSafeMutationsAsync(
        DbConnection connection,
        DatabaseObjectId objectId,
        CancellationToken cancellationToken)
    {
        var commandText = BuildMutationCapabilitySql(objectId);
        if (commandText.Length == 0)
        {
            return true;
        }

        await using var command = CreateCommand(connection, commandText, objectId);
        var engine = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return IsTransactionalMySqlEngine(engine);
    }

    internal string BuildMutationCapabilitySql(DatabaseObjectId objectId)
    {
        if (dialect.Family != DatabaseFamily.MySql)
        {
            return string.Empty;
        }

        var schema = dialect.ParameterMarker("schema");
        var table = dialect.ParameterMarker("table");
        return $"""
            SELECT engine
            FROM information_schema.tables
            WHERE table_schema = {schema} AND table_name = {table}
              AND table_type = 'BASE TABLE';
            """;
    }

    internal static bool IsTransactionalMySqlEngine(object? engine) =>
        string.Equals(
            Convert.ToString(engine, System.Globalization.CultureInfo.InvariantCulture),
            "InnoDB",
            StringComparison.OrdinalIgnoreCase);

    internal string BuildColumnsSql(DatabaseObjectId objectId)
    {
        var catalog = dialect.ParameterMarker("catalog");
        var schema = dialect.ParameterMarker("schema");
        var table = dialect.ParameterMarker(
            dialect.Family == DatabaseFamily.Oracle ? "object_name" : "table");
        var postgreSqlIdentity = dialect.SupportsPostgreSqlGeneratedColumns
            ? "CASE WHEN c.is_identity = 'YES' OR c.column_default LIKE 'nextval(%' THEN 1 ELSE 0 END"
            : "CASE WHEN c.column_default LIKE 'nextval(%' THEN 1 ELSE 0 END";
        var postgreSqlGenerated = dialect.SupportsPostgreSqlGeneratedColumns
            ? "CASE WHEN c.is_generated = 'ALWAYS' THEN 1 ELSE 0 END"
            : "0";
        return dialect.Family switch
        {
            DatabaseFamily.Sqlite => $"""
                WITH column_info AS (
                    SELECT * FROM pragma_table_xinfo({table})
                ), table_info AS (
                    SELECT wr FROM pragma_table_list({table})
                )
                SELECT name, type, cid + 1,
                       CASE WHEN "notnull" = 0 THEN 1 ELSE 0 END,
                       dflt_value, NULL, NULL, NULL,
                       CASE WHEN pk = 1 AND upper(trim(type)) = 'INTEGER'
                                  AND (SELECT count(*) FROM column_info WHERE pk > 0) = 1
                                  AND COALESCE((SELECT max(wr) FROM table_info), 0) = 0
                            THEN 1 ELSE 0 END,
                       CASE WHEN hidden IN (2, 3) THEN 1 ELSE 0 END,
                       CASE WHEN hidden <> 0 THEN 1 ELSE 0 END,
                       CASE WHEN pk > 0 THEN 1 ELSE 0 END,
                       CASE WHEN pk > 0 THEN pk ELSE NULL END
                FROM column_info
                ORDER BY cid;
                """,
            DatabaseFamily.PostgreSql => $"""
                WITH pk AS (
                    SELECT kcu.table_catalog, kcu.table_schema, kcu.table_name,
                           kcu.column_name, kcu.ordinal_position
                    FROM information_schema.table_constraints tc
                    JOIN information_schema.key_column_usage kcu
                      ON kcu.constraint_catalog = tc.constraint_catalog
                     AND kcu.constraint_schema = tc.constraint_schema
                     AND kcu.constraint_name = tc.constraint_name
                    WHERE tc.constraint_type = 'PRIMARY KEY'
                )
                SELECT c.column_name, c.data_type, c.ordinal_position,
                       CASE WHEN c.is_nullable = 'YES' THEN 1 ELSE 0 END,
                       c.column_default, c.character_maximum_length,
                       c.numeric_precision, c.numeric_scale,
                       {postgreSqlIdentity},
                       {postgreSqlGenerated}, {postgreSqlGenerated},
                       CASE WHEN pk.column_name IS NULL THEN 0 ELSE 1 END,
                       pk.ordinal_position
                FROM information_schema.columns c
                LEFT JOIN pk ON pk.table_catalog = c.table_catalog
                            AND pk.table_schema = c.table_schema
                            AND pk.table_name = c.table_name
                            AND pk.column_name = c.column_name
                WHERE c.table_schema = {schema} AND c.table_name = {table}
                ORDER BY c.ordinal_position;
                """,
            DatabaseFamily.MySql => $"""
                SELECT c.column_name, c.column_type, c.ordinal_position,
                       CASE WHEN c.is_nullable = 'YES' THEN 1 ELSE 0 END,
                       c.column_default, c.character_maximum_length,
                       c.numeric_precision, c.numeric_scale,
                       CASE WHEN c.extra LIKE '%auto_increment%' THEN 1 ELSE 0 END,
                       CASE WHEN COALESCE(c.generation_expression, '') <> '' THEN 1 ELSE 0 END,
                       CASE WHEN COALESCE(c.generation_expression, '') <> '' THEN 1 ELSE 0 END,
                       CASE WHEN c.column_key = 'PRI' THEN 1 ELSE 0 END,
                       s.seq_in_index
                FROM information_schema.columns c
                LEFT JOIN information_schema.statistics s
                  ON s.table_schema = c.table_schema AND s.table_name = c.table_name
                 AND s.index_name = 'PRIMARY' AND s.column_name = c.column_name
                WHERE c.table_schema = {schema} AND c.table_name = {table}
                ORDER BY c.ordinal_position;
                """,
            DatabaseFamily.SqlServer => $"""
                SELECT c.name, ty.name, c.column_id, c.is_nullable,
                       dc.definition,
                       CASE WHEN c.max_length = -1 THEN -1
                            WHEN c.system_type_id IN (231, 239) THEN c.max_length / 2
                            ELSE c.max_length END,
                       c.precision, c.scale,
                       c.is_identity,
                       CASE WHEN c.is_computed = 1 OR c.generated_always_type <> 0 THEN 1 ELSE 0 END,
                       CASE WHEN c.is_computed = 1 OR c.generated_always_type <> 0
                                  OR c.system_type_id = 189 THEN 1 ELSE 0 END,
                       CASE WHEN ic.column_id IS NULL THEN 0 ELSE 1 END,
                       ic.key_ordinal
                FROM sys.objects o
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                JOIN sys.columns c ON c.object_id = o.object_id
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
                LEFT JOIN sys.indexes i ON i.object_id = o.object_id AND i.is_primary_key = 1
                LEFT JOIN sys.index_columns ic
                  ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                 AND ic.column_id = c.column_id
                WHERE DB_NAME() = {catalog} AND s.name = {schema} AND o.name = {table}
                  AND o.type IN ('U', 'V')
                ORDER BY c.column_id;
                """,
            DatabaseFamily.DuckDb => $"""
                WITH pk AS (
                    SELECT kcu.table_catalog, kcu.table_schema, kcu.table_name,
                           kcu.column_name, kcu.ordinal_position
                    FROM information_schema.table_constraints tc
                    JOIN information_schema.key_column_usage kcu
                      ON kcu.constraint_catalog = tc.constraint_catalog
                     AND kcu.constraint_schema = tc.constraint_schema
                     AND kcu.constraint_name = tc.constraint_name
                    WHERE tc.constraint_type = 'PRIMARY KEY'
                )
                SELECT c.column_name, c.data_type, c.ordinal_position,
                       CASE WHEN c.is_nullable = 'YES' THEN 1 ELSE 0 END,
                       c.column_default, c.character_maximum_length,
                       c.numeric_precision, c.numeric_scale,
                       0, 0, 0,
                       CASE WHEN pk.column_name IS NULL THEN 0 ELSE 1 END,
                       pk.ordinal_position
                FROM information_schema.columns c
                LEFT JOIN pk ON pk.table_catalog = c.table_catalog
                            AND pk.table_schema = c.table_schema
                            AND pk.table_name = c.table_name
                            AND pk.column_name = c.column_name
                WHERE c.table_catalog = {catalog} AND c.table_schema = {schema}
                  AND c.table_name = {table}
                ORDER BY c.ordinal_position;
                """,
            DatabaseFamily.Oracle => $"""
                SELECT c.column_name, c.data_type, c.column_id, c.nullable,
                       c.data_default, c.char_length, c.data_precision, c.data_scale,
                       CASE WHEN ic.column_name IS NULL THEN 0 ELSE 1 END,
                       CASE WHEN c.virtual_column = 'YES' THEN 1 ELSE 0 END,
                       CASE WHEN c.virtual_column = 'YES' THEN 1 ELSE 0 END,
                       CASE WHEN pk.column_name IS NULL THEN 0 ELSE 1 END,
                       pk.position
                FROM all_tab_cols c
                LEFT JOIN all_tab_identity_cols ic
                  ON ic.owner = c.owner AND ic.table_name = c.table_name
                 AND ic.column_name = c.column_name
                LEFT JOIN (
                    SELECT cc.owner, cc.table_name, cc.column_name, cc.position
                    FROM all_constraints co
                    JOIN all_cons_columns cc
                      ON cc.owner = co.owner AND cc.constraint_name = co.constraint_name
                    WHERE co.constraint_type = 'P'
                ) pk ON pk.owner = c.owner AND pk.table_name = c.table_name
                    AND pk.column_name = c.column_name
                WHERE c.owner = {schema} AND c.table_name = {table}
                  AND c.hidden_column = 'NO'
                ORDER BY c.column_id
                """,
            DatabaseFamily.Firebird => $"""
                SELECT trim(rf.rdb$field_name),
                       CASE
                           WHEN f.rdb$field_type IN (7, 8, 16, 26)
                                AND f.rdb$field_sub_type = 1 THEN 'NUMERIC'
                           WHEN f.rdb$field_type IN (7, 8, 16, 26)
                                AND f.rdb$field_sub_type = 2 THEN 'DECIMAL'
                           WHEN f.rdb$field_type = 7 THEN 'SMALLINT'
                           WHEN f.rdb$field_type = 8 THEN 'INTEGER'
                           WHEN f.rdb$field_type = 10 THEN 'FLOAT'
                           WHEN f.rdb$field_type = 11 THEN 'DOUBLE PRECISION'
                           WHEN f.rdb$field_type = 12 THEN 'DATE'
                           WHEN f.rdb$field_type = 13 THEN 'TIME'
                           WHEN f.rdb$field_type = 14 THEN 'CHAR'
                           WHEN f.rdb$field_type = 16 THEN 'BIGINT'
                           WHEN f.rdb$field_type = 23 THEN 'BOOLEAN'
                           WHEN f.rdb$field_type = 24 THEN 'DECFLOAT(16)'
                           WHEN f.rdb$field_type = 25 THEN 'DECFLOAT(34)'
                           WHEN f.rdb$field_type = 26 THEN 'INT128'
                           WHEN f.rdb$field_type = 27 THEN 'DOUBLE PRECISION'
                           WHEN f.rdb$field_type = 28 THEN 'TIME WITH TIME ZONE'
                           WHEN f.rdb$field_type = 29 THEN 'TIMESTAMP WITH TIME ZONE'
                           WHEN f.rdb$field_type = 35 THEN 'TIMESTAMP'
                           WHEN f.rdb$field_type = 37 THEN 'VARCHAR'
                           WHEN f.rdb$field_type = 261
                                AND f.rdb$field_sub_type = 1 THEN 'TEXT'
                           WHEN f.rdb$field_type = 261 THEN 'BLOB'
                           ELSE 'UNKNOWN'
                       END,
                       rf.rdb$field_position + 1,
                       CASE WHEN COALESCE(rf.rdb$null_flag, 0) = 0 THEN 1 ELSE 0 END,
                       COALESCE(rf.rdb$default_source, f.rdb$default_source),
                       f.rdb$character_length, f.rdb$field_precision, -f.rdb$field_scale,
                       CASE WHEN rf.rdb$generator_name IS NULL THEN 0 ELSE 1 END,
                       CASE WHEN f.rdb$computed_source IS NULL THEN 0 ELSE 1 END,
                       CASE WHEN f.rdb$computed_source IS NULL THEN 0 ELSE 1 END,
                       CASE WHEN seg.rdb$field_name IS NULL THEN 0 ELSE 1 END,
                       seg.rdb$field_position + 1
                FROM rdb$relation_fields rf
                JOIN rdb$fields f ON f.rdb$field_name = rf.rdb$field_source
                LEFT JOIN rdb$relation_constraints rc
                  ON rc.rdb$relation_name = rf.rdb$relation_name
                 AND rc.rdb$constraint_type = 'PRIMARY KEY'
                LEFT JOIN rdb$index_segments seg
                  ON seg.rdb$index_name = rc.rdb$index_name
                 AND seg.rdb$field_name = rf.rdb$field_name
                WHERE trim(rf.rdb$relation_name) = {table}
                ORDER BY rf.rdb$field_position
                """,
            DatabaseFamily.ClickHouse => $"""
                SELECT name, type, position,
                       CASE WHEN startsWith(type, 'Nullable(') THEN 1 ELSE 0 END,
                       default_expression, NULL, NULL, NULL,
                       0,
                       CASE WHEN default_kind IN ('MATERIALIZED', 'ALIAS') THEN 1 ELSE 0 END,
                       CASE WHEN default_kind IN ('MATERIALIZED', 'ALIAS') THEN 1 ELSE 0 END,
                       is_in_primary_key,
                       CASE WHEN is_in_primary_key = 1 THEN position ELSE NULL END
                FROM system.columns
                WHERE database = {schema} AND table = {table}
                ORDER BY position;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(objectId)),
        };
    }

    internal string BuildIndexesSql(DatabaseObjectId objectId)
    {
        var catalog = dialect.ParameterMarker("catalog");
        var schema = dialect.ParameterMarker("schema");
        var table = dialect.ParameterMarker(
            dialect.Family == DatabaseFamily.Oracle ? "object_name" : "table");
        return dialect.Family switch
        {
            DatabaseFamily.Sqlite => $"""
                SELECT il.name,
                       CASE il.origin WHEN 'pk' THEN 'primary' WHEN 'u' THEN 'unique' ELSE 'index' END,
                       il."unique", CASE WHEN il.origin = 'pk' THEN 1 ELSE 0 END, 1,
                       ix.seqno + 1, ix.name, CASE WHEN ix.key = 0 THEN 1 ELSE 0 END,
                       ix.desc, CASE WHEN ix.cid = -2 THEN '<expression>' ELSE NULL END,
                       NULL, sm.sql
                FROM pragma_index_list({table}) il
                JOIN pragma_index_xinfo(il.name) ix ON ix.key = 1
                LEFT JOIN sqlite_master sm ON sm.type = 'index' AND sm.name = il.name
                ORDER BY il.seq, ix.seqno;
                """,
            DatabaseFamily.PostgreSql => $"""
                SELECT ci.relname, am.amname, ix.indisunique, ix.indisprimary, ix.indisvalid,
                       keys.ordinality, a.attname,
                       CASE WHEN keys.ordinality > ix.indnkeyatts THEN 1 ELSE 0 END,
                       CASE WHEN (ix.indoption[keys.ordinality - 1] & 1) = 1 THEN 1 ELSE 0 END,
                       CASE WHEN a.attname IS NULL THEN pg_get_indexdef(ix.indexrelid, keys.ordinality::integer, true) ELSE NULL END,
                       pg_get_expr(ix.indpred, ix.indrelid), pg_get_indexdef(ix.indexrelid)
                FROM pg_index ix
                JOIN pg_class t ON t.oid = ix.indrelid
                JOIN pg_namespace ns ON ns.oid = t.relnamespace
                JOIN pg_class ci ON ci.oid = ix.indexrelid
                JOIN pg_am am ON am.oid = ci.relam
                CROSS JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY keys(attnum, ordinality)
                LEFT JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = keys.attnum
                WHERE ns.nspname = {schema} AND t.relname = {table}
                ORDER BY ci.relname, keys.ordinality;
                """,
            DatabaseFamily.MySql => $"""
                SELECT s.index_name, s.index_type,
                       CASE WHEN s.non_unique = 0 THEN 1 ELSE 0 END,
                       CASE WHEN s.index_name = 'PRIMARY' THEN 1 ELSE 0 END,
                       1,
                       s.seq_in_index, s.column_name, 0,
                       CASE WHEN s.collation = 'D' THEN 1 ELSE 0 END,
                       NULL, NULL, NULL
                FROM information_schema.statistics s
                WHERE s.table_schema = {schema} AND s.table_name = {table}
                ORDER BY s.index_name, s.seq_in_index;
                """,
            DatabaseFamily.SqlServer => $"""
                SELECT i.name, i.type_desc, i.is_unique, i.is_primary_key,
                       CASE WHEN i.is_disabled = 0 THEN 1 ELSE 0 END,
                       ic.index_column_id, c.name, ic.is_included_column, ic.is_descending_key,
                       NULL, i.filter_definition, NULL
                FROM sys.objects o
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                JOIN sys.indexes i ON i.object_id = o.object_id AND i.index_id > 0
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE DB_NAME() = {catalog} AND s.name = {schema} AND o.name = {table}
                  AND o.type IN ('U', 'V')
                ORDER BY i.name, ic.index_column_id;
                """,
            DatabaseFamily.DuckDb => $"""
                SELECT index_name, 'index', is_unique, is_primary, 1,
                       1, NULL, 0, 0, expressions, NULL, sql
                FROM duckdb_indexes()
                WHERE database_name = {catalog} AND schema_name = {schema} AND table_name = {table}
                ORDER BY index_name;
                """,
            DatabaseFamily.Oracle => $"""
                SELECT i.index_name, i.index_type,
                       CASE WHEN i.uniqueness = 'UNIQUE' THEN 1 ELSE 0 END,
                       CASE WHEN co.constraint_type = 'P' THEN 1 ELSE 0 END,
                       CASE WHEN i.status = 'VALID' THEN 1 ELSE 0 END,
                       c.column_position, c.column_name, 0,
                       CASE WHEN c.descend = 'DESC' THEN 1 ELSE 0 END,
                       NULL, NULL, NULL
                FROM all_indexes i
                JOIN all_ind_columns c ON c.index_owner = i.owner AND c.index_name = i.index_name
                LEFT JOIN all_constraints co ON co.owner = i.owner AND co.index_name = i.index_name
                WHERE i.table_owner = {schema} AND i.table_name = {table}
                ORDER BY i.index_name, c.column_position
                """,
            DatabaseFamily.Firebird => $"""
                SELECT trim(i.rdb$index_name), 'index',
                       CASE WHEN COALESCE(i.rdb$unique_flag, 0) = 1 THEN 1 ELSE 0 END,
                       CASE WHEN rc.rdb$constraint_type = 'PRIMARY KEY' THEN 1 ELSE 0 END,
                       CASE WHEN COALESCE(i.rdb$index_inactive, 0) = 0 THEN 1 ELSE 0 END,
                       seg.rdb$field_position + 1, trim(seg.rdb$field_name), 0,
                       CASE WHEN COALESCE(i.rdb$index_type, 0) = 1 THEN 1 ELSE 0 END,
                       i.rdb$expression_source, NULL, NULL
                FROM rdb$indices i
                LEFT JOIN rdb$index_segments seg ON seg.rdb$index_name = i.rdb$index_name
                LEFT JOIN rdb$relation_constraints rc ON rc.rdb$index_name = i.rdb$index_name
                WHERE trim(i.rdb$relation_name) = {table}
                ORDER BY i.rdb$index_name, seg.rdb$field_position
                """,
            DatabaseFamily.ClickHouse => $"""
                SELECT name, type, 0, 0, 1, 1,
                       CAST(NULL AS Nullable(String)), 0, 0,
                       CAST(expr AS Nullable(String)),
                       CAST(NULL AS Nullable(String)),
                       CAST(NULL AS Nullable(String))
                FROM system.data_skipping_indices
                WHERE database = {schema} AND table = {table}
                UNION ALL
                SELECT 'primary_key', 'primary', 0, 1, 1, 1,
                       CAST(NULL AS Nullable(String)), 0, 0,
                       CAST(primary_key AS Nullable(String)),
                       CAST(NULL AS Nullable(String)),
                       CAST(create_table_query AS Nullable(String))
                FROM system.tables
                WHERE database = {schema} AND name = {table} AND primary_key <> ''
                UNION ALL
                SELECT 'sorting_key', 'sorting', 0, 0, 1, 1,
                       CAST(NULL AS Nullable(String)), 0, 0,
                       CAST(sorting_key AS Nullable(String)),
                       CAST(NULL AS Nullable(String)),
                       CAST(create_table_query AS Nullable(String))
                FROM system.tables
                WHERE database = {schema} AND name = {table} AND sorting_key <> ''
                ORDER BY 1;
                """,
            _ => string.Empty,
        };
    }

    internal string BuildForeignKeysSql(DatabaseObjectId objectId)
    {
        var catalog = dialect.ParameterMarker("catalog");
        var schema = dialect.ParameterMarker("schema");
        var table = dialect.ParameterMarker(
            dialect.Family == DatabaseFamily.Oracle ? "object_name" : "table");
        return dialect.Family switch
        {
            DatabaseFamily.Sqlite => $"""
                SELECT 'fk_' || id, "from", NULL, NULL, "table", "to", seq + 1
                FROM pragma_foreign_key_list({table})
                ORDER BY id, seq;
                """,
            DatabaseFamily.PostgreSql => $"""
                SELECT tc.constraint_name, kcu.column_name,
                       ccu.table_catalog, ccu.table_schema, ccu.table_name, ccu.column_name,
                       kcu.ordinal_position
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu
                  ON kcu.constraint_catalog = tc.constraint_catalog
                 AND kcu.constraint_schema = tc.constraint_schema
                 AND kcu.constraint_name = tc.constraint_name
                JOIN information_schema.referential_constraints rc
                  ON rc.constraint_catalog = tc.constraint_catalog
                 AND rc.constraint_schema = tc.constraint_schema
                 AND rc.constraint_name = tc.constraint_name
                JOIN information_schema.key_column_usage ccu
                  ON ccu.constraint_catalog = rc.unique_constraint_catalog
                 AND ccu.constraint_schema = rc.unique_constraint_schema
                 AND ccu.constraint_name = rc.unique_constraint_name
                 AND ccu.ordinal_position = kcu.position_in_unique_constraint
                WHERE tc.constraint_type = 'FOREIGN KEY'
                  AND tc.table_catalog = {catalog}
                  AND tc.table_schema = {schema}
                  AND tc.table_name = {table}
                ORDER BY tc.constraint_name, kcu.ordinal_position;
                """,
            DatabaseFamily.MySql => $"""
                SELECT kcu.constraint_name, kcu.column_name,
                       NULL, kcu.referenced_table_schema,
                       kcu.referenced_table_name, kcu.referenced_column_name,
                       kcu.ordinal_position
                FROM information_schema.key_column_usage kcu
                WHERE kcu.table_schema = {schema} AND kcu.table_name = {table}
                  AND kcu.referenced_table_name IS NOT NULL
                ORDER BY kcu.constraint_name, kcu.ordinal_position;
                """,
            DatabaseFamily.SqlServer => $"""
                SELECT fk.name, child_column.name,
                       DB_NAME(), parent_schema.name, parent_table.name, parent_column.name,
                       fkc.constraint_column_id
                FROM sys.foreign_keys fk
                JOIN sys.foreign_key_columns fkc
                  ON fkc.constraint_object_id = fk.object_id
                JOIN sys.tables child_table ON child_table.object_id = fk.parent_object_id
                JOIN sys.schemas child_schema ON child_schema.schema_id = child_table.schema_id
                JOIN sys.columns child_column
                  ON child_column.object_id = child_table.object_id
                 AND child_column.column_id = fkc.parent_column_id
                JOIN sys.tables parent_table ON parent_table.object_id = fk.referenced_object_id
                JOIN sys.schemas parent_schema ON parent_schema.schema_id = parent_table.schema_id
                JOIN sys.columns parent_column
                  ON parent_column.object_id = parent_table.object_id
                 AND parent_column.column_id = fkc.referenced_column_id
                WHERE DB_NAME() = {catalog} AND child_schema.name = {schema}
                  AND child_table.name = {table}
                ORDER BY fk.name, fkc.constraint_column_id;
                """,
            DatabaseFamily.DuckDb => $"""
                SELECT tc.constraint_name, child.column_name,
                       parent.table_catalog, parent.table_schema, parent.table_name, parent.column_name,
                       child.ordinal_position
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage child
                  ON child.constraint_catalog = tc.constraint_catalog
                 AND child.constraint_schema = tc.constraint_schema
                 AND child.constraint_name = tc.constraint_name
                JOIN information_schema.referential_constraints rc
                  ON rc.constraint_catalog = tc.constraint_catalog
                 AND rc.constraint_schema = tc.constraint_schema
                 AND rc.constraint_name = tc.constraint_name
                JOIN information_schema.key_column_usage parent
                  ON parent.constraint_catalog = rc.unique_constraint_catalog
                 AND parent.constraint_schema = rc.unique_constraint_schema
                 AND parent.constraint_name = rc.unique_constraint_name
                 AND parent.ordinal_position = child.position_in_unique_constraint
                WHERE tc.constraint_type = 'FOREIGN KEY'
                  AND tc.table_catalog = {catalog} AND tc.table_schema = {schema}
                  AND tc.table_name = {table}
                ORDER BY tc.constraint_name, child.ordinal_position;
                """,
            DatabaseFamily.Oracle => $"""
                SELECT child.constraint_name, child_column.column_name,
                       NULL, parent.owner, parent.table_name, parent_column.column_name,
                       child_column.position
                FROM all_constraints child
                JOIN all_cons_columns child_column
                  ON child_column.owner = child.owner
                 AND child_column.constraint_name = child.constraint_name
                JOIN all_constraints parent
                  ON parent.owner = child.r_owner
                 AND parent.constraint_name = child.r_constraint_name
                JOIN all_cons_columns parent_column
                  ON parent_column.owner = parent.owner
                 AND parent_column.constraint_name = parent.constraint_name
                 AND parent_column.position = child_column.position
                WHERE child.constraint_type = 'R'
                  AND child.owner = {schema} AND child.table_name = {table}
                ORDER BY child.constraint_name, child_column.position
                """,
            DatabaseFamily.Firebird => $"""
                SELECT trim(child.rdb$constraint_name), trim(child_segment.rdb$field_name),
                       NULL, NULL, trim(parent.rdb$relation_name), trim(parent_segment.rdb$field_name),
                       child_segment.rdb$field_position + 1
                FROM rdb$relation_constraints child
                JOIN rdb$ref_constraints reference
                  ON reference.rdb$constraint_name = child.rdb$constraint_name
                JOIN rdb$relation_constraints parent
                  ON parent.rdb$constraint_name = reference.rdb$const_name_uq
                JOIN rdb$index_segments child_segment
                  ON child_segment.rdb$index_name = child.rdb$index_name
                JOIN rdb$index_segments parent_segment
                  ON parent_segment.rdb$index_name = parent.rdb$index_name
                 AND parent_segment.rdb$field_position = child_segment.rdb$field_position
                WHERE child.rdb$constraint_type = 'FOREIGN KEY'
                  AND trim(child.rdb$relation_name) = {table}
                ORDER BY child.rdb$constraint_name, child_segment.rdb$field_position
                """,
            _ => string.Empty,
        };
    }

    private static string? ReadString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToString(
                reader.GetValue(ordinal),
                System.Globalization.CultureInfo.InvariantCulture);

    private static int? ReadInt32(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static long? ReadInt64(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static bool ReadBoolean(DbDataReader reader, int ordinal) =>
        ReadNullableBoolean(reader, ordinal) == true;

    private static bool? ReadNullableBoolean(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) switch
        {
            bool value => value,
            string value => value.Equals("YES", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Y", StringComparison.OrdinalIgnoreCase)
                || value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "1", StringComparison.Ordinal),
            var value => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0,
        };
    }

    private sealed record IndexRow(
        string Name,
        string Kind,
        bool IsUnique,
        bool IsPrimary,
        bool IsValid,
        int Ordinal,
        string? ColumnName,
        bool IsIncluded,
        bool IsDescending,
        string? Expression,
        string? Predicate,
        string? Definition);

    private sealed record ForeignKeyRow(
        string Name,
        string ColumnName,
        string? ReferencedCatalog,
        string? ReferencedSchema,
        string ReferencedTable,
        string ReferencedColumnName,
        int Ordinal);
}
