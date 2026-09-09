using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Asura.Application;
using Asura.Core;

namespace Asura.Databases;

/// <summary>
/// The generic ADO.NET query surface behind <see cref="IDatabasePanelClient"/>.
/// Connections are opened per call — ADO.NET pooling makes that cheap for the
/// server drivers, and it keeps a panel from pinning a file lock or a socket
/// while idle. Routed callers select a connection backend before any provider
/// opens; this client never rewrites endpoints or supplies driver sockets.
/// </summary>
public sealed partial class DatabasePanelClient : IDatabasePanelClient, IAsyncDisposable
{
    private const int MaximumTablePageSize = 5000;
    private const int MaximumSqlCatalogObjects = 1000;
    private const int MaximumSqlCatalogColumns = 50_000;
    private const int MaximumSqlCatalogRoutines = 5000;
    private const int MaximumSqlCatalogRoutineParameters = 20_000;
    private const int MaximumSqlCatalogParametersPerRoutine = 1024;
    private const int MaximumSqlCatalogIntrinsicSymbols = 5000;
    private const int MaximumSqlCatalogMetadataUtf8Bytes = 6 * 1024 * 1024;
    private const int MaximumRoutineSignatureCharacters = 2048;
    private const int MaximumRoutineTypeNameCharacters = 512;
    private const int MaximumRoutineParameterNameCharacters = 256;
    private static readonly TimeSpan SqlCatalogExtractionTimeout = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyDictionary<string, IDatabaseDriver> _drivers;
    private readonly ConnectionProfile? _defaultTunnel;
    private readonly IDatabaseDiagramWorkerFactory? _diagramWorkers;
    private readonly Func<DatabaseValueContentStore>? _contentStoreFactory;
    private readonly IDatabaseOperationExecutor? _operationExecutor;
    private readonly Func<DatabaseDriverDescriptor, ConnectionProfile?, CancellationToken, ValueTask<IDatabaseOperationExecutor>>? _operationExecutorSelector;
    private bool _disposed;

    public DatabasePanelClient(
        ConnectionProfile? defaultTunnel = null,
        IDatabaseDiagramWorkerFactory? diagramWorkers = null,
        Func<DatabaseValueContentStore>? contentStoreFactory = null,
        IDatabaseOperationExecutor? operationExecutor = null,
        Func<DatabaseDriverDescriptor, ConnectionProfile?, CancellationToken, ValueTask<IDatabaseOperationExecutor>>? operationExecutorSelector = null)
        : this(BuiltInDatabaseDrivers.All, defaultTunnel, diagramWorkers, contentStoreFactory, operationExecutor, operationExecutorSelector)
    {
    }

    public DatabasePanelClient(
        IReadOnlyList<IDatabaseDriver> drivers,
        ConnectionProfile? defaultTunnel = null,
        IDatabaseDiagramWorkerFactory? diagramWorkers = null,
        Func<DatabaseValueContentStore>? contentStoreFactory = null,
        IDatabaseOperationExecutor? operationExecutor = null,
        Func<DatabaseDriverDescriptor, ConnectionProfile?, CancellationToken, ValueTask<IDatabaseOperationExecutor>>? operationExecutorSelector = null)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        _drivers = drivers.ToDictionary(
            driver => driver.Descriptor.Id,
            StringComparer.Ordinal);
        _defaultTunnel = defaultTunnel;
        _diagramWorkers = diagramWorkers;
        _contentStoreFactory = contentStoreFactory;
        _operationExecutor = operationExecutor;
        _operationExecutorSelector = operationExecutorSelector;
        Drivers = [.. drivers.Select(driver => driver.Descriptor)];
    }

    public IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; }

    private bool ExecutesInWorker => _operationExecutorSelector is not null || _operationExecutor is not null;

    public async Task<IReadOnlyList<DatabaseTableDescriptor>> ListTablesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken)
    {
        var driver = Resolve(driverId);
        if (ExecutesInWorker)
        {
            return await ExecuteInWorkerAsync(driver, connectionString, tunnel,
                (executor, target, token) => executor.ListTablesAsync(target, token),
                cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteWithConnectionAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (createConnection, token) =>
            {
                await using var connection = createConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                return await ReadTablesAsync(connection, driver, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseSchemaGraph> GetDatabaseSchemaGraphAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken)
    {
        var driver = Resolve(driverId);
        if (ExecutesInWorker)
        {
            return await ExecuteInWorkerAsync(driver, connectionString, tunnel,
                (executor, target, token) => executor.GetDatabaseSchemaGraphAsync(target, token),
                cancellationToken).ConfigureAwait(false);
        }

        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteWithConnectionAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (createConnection, token) =>
            {
                await using var connection = createConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                var objects = await ReadTablesAsync(connection, driver, token).ConfigureAwait(false);
                var reader = new DatabaseMetadataReader(dialect);
                var tables = new List<DatabaseSchemaTable>();
                foreach (var databaseObject in objects.Where(candidate =>
                             candidate.Kind == DatabaseTableKind.Table))
                {
                    tables.Add(await reader
                        .ReadSchemaTableAsync(connection, databaseObject, token)
                        .ConfigureAwait(false));
                }

                return new DatabaseSchemaGraph(tables);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<SqlCatalogSnapshot> GetSqlCatalogAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken)
    {
        var driver = Resolve(driverId);
        if (ExecutesInWorker)
        {
            return await ExecuteInWorkerAsync(driver, connectionString, tunnel,
                (executor, target, token) => executor.GetSqlCatalogAsync(target, token),
                cancellationToken).ConfigureAwait(false);
        }

        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteWithConnectionAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (createConnection, token) =>
            {
                await using var connection = createConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                var descriptors = await ReadTablesAsync(connection, driver, token)
                    .ConfigureAwait(false);
                var defaults = await ReadSqlCatalogDefaultsAsync(
                        connection,
                        driver,
                        token)
                    .ConfigureAwait(false);
                var metadata = new DatabaseMetadataReader(dialect);
                var objects = new List<SqlCatalogObject>(Math.Min(
                    descriptors.Count,
                    MaximumSqlCatalogObjects));
                var totalColumns = 0;
                var estimatedUtf8Bytes = 0;
                var isPartial = descriptors.Count > MaximumSqlCatalogObjects;
                string? limitation = isPartial
                    ? $"Only the first {MaximumSqlCatalogObjects} of {descriptors.Count} objects were loaded."
                    : null;
                using var extractionTimeout = CancellationTokenSource
                    .CreateLinkedTokenSource(token);
                extractionTimeout.CancelAfter(SqlCatalogExtractionTimeout);
                foreach (var descriptor in descriptors)
                {
                    if (objects.Count >= MaximumSqlCatalogObjects)
                    {
                        break;
                    }

                    IReadOnlyList<DatabaseColumnSchema> columns;
                    try
                    {
                        columns = await metadata
                            .ReadColumnsOnlyAsync(
                                connection,
                                descriptor.Id,
                                extractionTimeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        !token.IsCancellationRequested
                        && extractionTimeout.IsCancellationRequested)
                    {
                        isPartial = true;
                        limitation = $"Catalog extraction stopped after {SqlCatalogExtractionTimeout.TotalSeconds:0} seconds.";
                        break;
                    }

                    var detachedColumns = columns
                        .OrderBy(column => column.Ordinal)
                        .Select(column => new SqlCatalogColumn(
                            column.Name,
                            column.DataTypeName,
                            column.ValueKind,
                            column.IsNullable))
                        .ToArray();
                    var objectBytes = EstimateSqlCatalogObjectUtf8Bytes(
                        descriptor.Id,
                        detachedColumns);
                    if (totalColumns + detachedColumns.Length > MaximumSqlCatalogColumns
                        || estimatedUtf8Bytes + objectBytes > MaximumSqlCatalogMetadataUtf8Bytes)
                    {
                        isPartial = true;
                        limitation = "The catalog reached its safe metadata size limit.";
                        break;
                    }

                    totalColumns += detachedColumns.Length;
                    estimatedUtf8Bytes += objectBytes;
                    objects.Add(new SqlCatalogObject(
                        CatalogObjectId(driverId, descriptor.Id),
                        descriptor.Kind,
                        detachedColumns));
                }

                IReadOnlyList<SqlCatalogRoutine> routines = [];
                var routineCoverage = driver.ListRoutinesSql is null
                    ? SqlCatalogCoverage.None
                    : driver.RoutineCatalogCoverage;
                if (!extractionTimeout.IsCancellationRequested
                    && driver.ListRoutinesSql is { } routinesSql)
                {
                    try
                    {
                        var routineCatalog = await ReadSqlCatalogRoutinesAsync(
                                connection,
                                driverId,
                                routinesSql,
                                MaximumSqlCatalogMetadataUtf8Bytes - estimatedUtf8Bytes,
                                extractionTimeout.Token)
                            .ConfigureAwait(false);
                        routines = routineCatalog.Routines;
                        estimatedUtf8Bytes += routineCatalog.EstimatedUtf8Bytes;
                        if (routineCatalog.IsPartial)
                        {
                            routineCoverage = SqlCatalogCoverage.Partial;
                            isPartial = true;
                            limitation = AppendCatalogLimitation(
                                limitation,
                                routineCatalog.Limitation
                                    ?? "The routine catalog reached its safe metadata limit.");
                        }
                    }
                    catch (OperationCanceledException) when (
                        !token.IsCancellationRequested
                        && extractionTimeout.IsCancellationRequested)
                    {
                        routineCoverage = SqlCatalogCoverage.Partial;
                        isPartial = true;
                        limitation = AppendCatalogLimitation(
                            limitation,
                            $"Routine extraction stopped after "
                                + $"{SqlCatalogExtractionTimeout.TotalSeconds:0} seconds.");
                    }
                    catch (DbException)
                    {
                        // Routine discovery is additive. Compatibility servers
                        // may reject a family query, so tables still initialize
                        // the language worker with an empty routine catalog;
                        // mark that loss so editor status does not imply that
                        // zero routines is complete server metadata.
                        routineCoverage = SqlCatalogCoverage.Partial;
                        isPartial = true;
                        limitation = AppendCatalogLimitation(
                            limitation,
                            "Routine metadata was unavailable for this connection.");
                    }
                }

                IReadOnlyList<SqlCatalogIntrinsicSymbol> intrinsicSymbols = [];
                var intrinsicCoverage = driver.ListIntrinsicSymbolsSql is null
                    ? SqlCatalogCoverage.None
                    : driver.IntrinsicCatalogCoverage;
                if (!extractionTimeout.IsCancellationRequested
                    && driver.ListIntrinsicSymbolsSql is { } intrinsicSql)
                {
                    try
                    {
                        var intrinsicCatalog = await ReadSqlCatalogIntrinsicSymbolsAsync(
                                connection,
                                intrinsicSql,
                                MaximumSqlCatalogMetadataUtf8Bytes - estimatedUtf8Bytes,
                                extractionTimeout.Token)
                            .ConfigureAwait(false);
                        intrinsicSymbols = intrinsicCatalog.Symbols;
                        estimatedUtf8Bytes += intrinsicCatalog.EstimatedUtf8Bytes;
                        if (intrinsicCatalog.IsPartial)
                        {
                            intrinsicCoverage = SqlCatalogCoverage.Partial;
                            isPartial = true;
                            limitation = AppendCatalogLimitation(
                                limitation,
                                "The intrinsic-symbol catalog reached its safe metadata limit.");
                        }
                    }
                    catch (OperationCanceledException) when (
                        !token.IsCancellationRequested
                        && extractionTimeout.IsCancellationRequested)
                    {
                        intrinsicCoverage = SqlCatalogCoverage.Partial;
                        isPartial = true;
                        limitation = AppendCatalogLimitation(
                            limitation,
                            $"Intrinsic-symbol extraction stopped after "
                                + $"{SqlCatalogExtractionTimeout.TotalSeconds:0} seconds.");
                    }
                    catch (DbException)
                    {
                        intrinsicCoverage = SqlCatalogCoverage.Partial;
                        isPartial = true;
                        limitation = AppendCatalogLimitation(
                            limitation,
                            "Intrinsic SQL metadata was unavailable for this connection.");
                    }
                }

                if (extractionTimeout.IsCancellationRequested)
                {
                    routineCoverage = DowngradeCatalogCoverage(routineCoverage);
                    intrinsicCoverage = DowngradeCatalogCoverage(intrinsicCoverage);
                }

                // PostgreSQL-family authority is useful only as a pair: pg_proc
                // covers calls while pg_get_keywords proves special bare values.
                if (driver.RoutineCatalogCoverage == SqlCatalogCoverage.Complete
                    && driver.IntrinsicCatalogCoverage == SqlCatalogCoverage.Complete
                    && (routineCoverage != SqlCatalogCoverage.Complete
                        || intrinsicCoverage != SqlCatalogCoverage.Complete))
                {
                    routineCoverage = DowngradeCatalogCoverage(routineCoverage);
                    intrinsicCoverage = DowngradeCatalogCoverage(intrinsicCoverage);
                }

                return new SqlCatalogSnapshot(
                    driverId,
                    defaults.Catalog,
                    defaults.Schema,
                    objects,
                    isPartial,
                    limitation)
                {
                    Routines = routines,
                    RoutineCoverage = routineCoverage,
                    IntrinsicSymbols = intrinsicSymbols,
                    IntrinsicCoverage = intrinsicCoverage,
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(string? Catalog, string? Schema)>
        ReadSqlCatalogDefaultsAsync(
            DbConnection connection,
            IDatabaseDriver driver,
            CancellationToken cancellationToken)
    {
        if (driver.SqlCatalogDefaultsSql is not { } sql)
        {
            return (null, null);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return (null, null);
            }

            return (
                ReadNullableString(reader, 0),
                ReadNullableString(reader, 1));
        }
        catch (DbException)
        {
            // Namespace discovery is advisory. A compatibility server may
            // reject its wire-family probe; returning no default makes the
            // worker resolve only unambiguous names instead of guessing.
            return (null, null);
        }
    }

    private static string? ReadNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static DatabaseObjectId CatalogObjectId(
        string driverId,
        DatabaseObjectId objectId) =>
        string.Equals(driverId, "sqlite", StringComparison.Ordinal)
            ? objectId with { Schema = objectId.Schema ?? "main" }
            : objectId;

    private static int EstimateSqlCatalogObjectUtf8Bytes(
        DatabaseObjectId id,
        IReadOnlyList<SqlCatalogColumn> columns)
    {
        const int JsonOverheadPerObject = 96;
        const int JsonOverheadPerColumn = 80;
        var bytes = JsonOverheadPerObject
            + Utf8Length(id.Catalog)
            + Utf8Length(id.Schema)
            + Utf8Length(id.Name);
        foreach (var column in columns)
        {
            bytes = checked(bytes
                + JsonOverheadPerColumn
                + Utf8Length(column.Name)
                + Utf8Length(column.DataTypeName));
        }

        return bytes;
    }

    private static int Utf8Length(string? value) =>
        value is null ? 0 : System.Text.Encoding.UTF8.GetByteCount(value);

    private static async Task<RoutineCatalogReadResult> ReadSqlCatalogRoutinesAsync(
        DbConnection connection,
        string driverId,
        string sql,
        int remainingUtf8Bytes,
        CancellationToken cancellationToken)
    {
        if (remainingUtf8Bytes <= 0)
        {
            return new RoutineCatalogReadResult(
                [],
                IsPartial: true,
                0,
                "The routine catalog had no remaining metadata budget.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var builders = new Dictionary<RoutineKey, RoutineBuilder>();
        var totalParameters = 0;
        var isPartial = false;
        string? partialReason = null;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = ReadRoutineText(reader, 2, MaximumRoutineParameterNameCharacters);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var id = CatalogObjectId(
                driverId,
                new DatabaseObjectId(
                    ReadRoutineText(reader, 0, MaximumRoutineParameterNameCharacters),
                    ReadRoutineText(reader, 1, MaximumRoutineParameterNameCharacters),
                    name));
            var signature = ReadRoutineText(
                    reader,
                    4,
                    MaximumRoutineSignatureCharacters)
                ?? name;
            var returnTypeName = ReadRoutineText(
                reader,
                5,
                MaximumRoutineTypeNameCharacters);
            var key = new RoutineKey(
                id,
                ParseRoutineKind(ReadRoutineText(reader, 3, 32)),
                signature,
                returnTypeName,
                reader.FieldCount > 13 && !reader.IsDBNull(12),
                ReadRoutineNullableInteger(reader, 12),
                ReadRoutineNullableInteger(reader, 13),
                ReadRoutineText(reader, 14, MaximumRoutineParameterNameCharacters));
            if (!builders.TryGetValue(key, out var builder))
            {
                if (builders.Count >= MaximumSqlCatalogRoutines)
                {
                    isPartial = true;
                    partialReason = "The routine catalog reached its safe routine-count limit.";
                    break;
                }

                builder = new RoutineBuilder(key);
                builders.Add(key, builder);
            }

            var parameterType = ReadRoutineText(
                reader,
                8,
                MaximumRoutineTypeNameCharacters);
            if (parameterType is null)
            {
                continue;
            }

            if (totalParameters >= MaximumSqlCatalogRoutineParameters)
            {
                isPartial = true;
                partialReason = "The routine catalog reached its safe parameter-count limit.";
                break;
            }

            totalParameters++;
            builder.Parameters.Add(new RoutineParameterRow(
                ReadRoutineOrdinal(reader, 6),
                new SqlCatalogRoutineParameter(
                    ReadRoutineText(
                        reader,
                        7,
                        MaximumRoutineParameterNameCharacters),
                    parameterType,
                    RoutineValueKind(parameterType),
                    ParseRoutineParameterMode(ReadRoutineText(reader, 9, 32)),
                    ReadRoutineBoolean(reader, 10),
                    ReadRoutineBoolean(reader, 11))));
        }

        var routines = new List<SqlCatalogRoutine>(builders.Count);
        var materializedRoutines = new HashSet<SqlCatalogRoutine>(
            SqlCatalogRoutineSemanticComparer.Instance);
        var estimatedUtf8Bytes = 0;
        foreach (var builder in builders.Values
                     .OrderBy(item => item.Key.Id.Catalog, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Id.Schema, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Id.Name, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.Signature, StringComparer.Ordinal))
        {
            var parameters = builder.Parameters
                .OrderBy(item => item.Ordinal)
                .Select(item => item.Parameter)
                .ToArray();
            var signature = string.Equals(
                builder.Key.Signature,
                builder.Key.Id.Name,
                StringComparison.Ordinal)
                ? BuildRoutineSignature(builder.Key.Id.Name, parameters)
                : builder.Key.Signature;
            var callableParameters = parameters.Where(parameter =>
                    parameter.Mode is SqlCatalogRoutineParameterMode.In
                        or SqlCatalogRoutineParameterMode.InOut
                        or SqlCatalogRoutineParameterMode.Unknown)
                .ToArray();
            if (parameters.Length > MaximumSqlCatalogParametersPerRoutine
                || callableParameters.Length > MaximumSqlCatalogParametersPerRoutine
                || !HasValidRoutineParameterShape(callableParameters))
            {
                isPartial = true;
                partialReason ??= $"The routine catalog omitted '{builder.Key.Id.DisplayName}' "
                    + "because its parameter metadata is inconsistent.";
                continue;
            }

            var derivedMinimumArgumentCount = callableParameters.Count(parameter =>
                !parameter.IsOptional && !parameter.IsVariadic);
            int? derivedMaximumArgumentCount = callableParameters.Any(parameter =>
                parameter.IsVariadic)
                ? null
                : callableParameters.Length;
            if (builder.Key.HasExplicitArity
                && builder.Key.MinimumArgumentCount is not (>= 0))
            {
                isPartial = true;
                partialReason ??= $"The routine catalog omitted '{builder.Key.Id.DisplayName}' "
                    + "because its argument-count metadata is invalid.";
                continue;
            }

            var minimumArgumentCount = builder.Key.HasExplicitArity
                ? builder.Key.MinimumArgumentCount!.Value
                : derivedMinimumArgumentCount;
            var maximumArgumentCount = builder.Key.HasExplicitArity
                ? builder.Key.MaximumArgumentCount
                : derivedMaximumArgumentCount;
            if (builder.Key.HasExplicitArity
                && parameters.Length > 0
                && (minimumArgumentCount != derivedMinimumArgumentCount
                    || maximumArgumentCount != derivedMaximumArgumentCount))
            {
                isPartial = true;
                partialReason ??= $"The routine catalog omitted '{builder.Key.Id.DisplayName}' "
                    + "because its argument counts contradict its parameters.";
                continue;
            }

            if (minimumArgumentCount > MaximumSqlCatalogParametersPerRoutine
                || maximumArgumentCount is { } maximum
                    && (maximum < 0
                        || maximum < minimumArgumentCount
                        || maximum > MaximumSqlCatalogParametersPerRoutine))
            {
                isPartial = true;
                partialReason ??= $"The routine catalog omitted '{builder.Key.Id.DisplayName}' "
                    + "because its argument count exceeds the safety bound.";
                continue;
            }

            var routine = new SqlCatalogRoutine(
                builder.Key.Id,
                builder.Key.Kind,
                signature,
                parameters,
                builder.Key.ReturnTypeName,
                RoutineValueKind(builder.Key.ReturnTypeName),
                minimumArgumentCount,
                maximumArgumentCount);
            if (!materializedRoutines.Add(routine))
            {
                continue;
            }

            var routineBytes = EstimateSqlCatalogRoutineUtf8Bytes(routine);
            if (estimatedUtf8Bytes + routineBytes > remainingUtf8Bytes)
            {
                isPartial = true;
                partialReason ??= "The routine catalog reached its safe metadata size limit.";
                break;
            }

            estimatedUtf8Bytes += routineBytes;
            routines.Add(routine);
        }

        return new RoutineCatalogReadResult(
            routines,
            isPartial,
            estimatedUtf8Bytes,
            partialReason);
    }

    private static bool HasValidRoutineParameterShape(
        IReadOnlyList<SqlCatalogRoutineParameter> callableParameters)
    {
        var optionalSeen = false;
        var variadicSeen = false;
        foreach (var parameter in callableParameters)
        {
            if (variadicSeen)
            {
                return false;
            }

            if (parameter.IsVariadic)
            {
                variadicSeen = true;
                continue;
            }

            if (optionalSeen && !parameter.IsOptional)
            {
                return false;
            }

            optionalSeen |= parameter.IsOptional;
        }

        return true;
    }

    private static async Task<IntrinsicCatalogReadResult>
        ReadSqlCatalogIntrinsicSymbolsAsync(
            DbConnection connection,
            string sql,
            int remainingUtf8Bytes,
            CancellationToken cancellationToken)
    {
        if (remainingUtf8Bytes <= 0)
        {
            return new IntrinsicCatalogReadResult([], IsPartial: true, 0);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var symbols = new Dictionary<
            (string Name, SqlCatalogIntrinsicKind Kind),
            SqlCatalogIntrinsicSymbol>(
            IntrinsicSymbolKeyComparer.Instance);
        var estimatedUtf8Bytes = 0;
        var isPartial = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = ReadRoutineText(
                reader,
                0,
                MaximumRoutineParameterNameCharacters);
            var kind = ParseIntrinsicKind(reader.FieldCount > 1
                ? ReadRoutineText(reader, 1, 32)
                : null) ?? SqlCatalogIntrinsicKind.Keyword;
            if (name is null)
            {
                continue;
            }

            var key = (name, kind);
            if (symbols.ContainsKey(key))
            {
                continue;
            }

            if (symbols.Count >= MaximumSqlCatalogIntrinsicSymbols)
            {
                isPartial = true;
                break;
            }

            var symbolBytes = 64 + Utf8Length(name);
            if (estimatedUtf8Bytes + symbolBytes > remainingUtf8Bytes)
            {
                isPartial = true;
                break;
            }

            estimatedUtf8Bytes += symbolBytes;
            symbols.Add(key, new SqlCatalogIntrinsicSymbol(name, kind));
        }

        return new IntrinsicCatalogReadResult(
            [.. symbols.Values
                .OrderBy(symbol => symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(symbol => symbol.Kind)],
            isPartial || symbols.Count == 0,
            estimatedUtf8Bytes);
    }

    private static string BuildRoutineSignature(
        string name,
        IReadOnlyList<SqlCatalogRoutineParameter> parameters) =>
        $"{name}({string.Join(", ", parameters.Select(parameter =>
            string.IsNullOrWhiteSpace(parameter.Name)
                ? parameter.DataTypeName
                : $"{parameter.Name} {parameter.DataTypeName}"))})";

    private static string? ReadRoutineText(
        DbDataReader reader,
        int ordinal,
        int maximumCharacters)
    {
        if (ordinal >= reader.FieldCount || reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = Convert.ToString(
                reader.GetValue(ordinal),
                CultureInfo.InvariantCulture)
            ?.TrimEnd();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters];
    }

    private static int ReadRoutineOrdinal(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? int.MaxValue
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static int? ReadRoutineNullableInteger(DbDataReader reader, int ordinal) =>
        ordinal >= reader.FieldCount || reader.IsDBNull(ordinal)
            ? null
            : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static bool ReadRoutineBoolean(DbDataReader reader, int ordinal)
    {
        if (ordinal >= reader.FieldCount || reader.IsDBNull(ordinal))
        {
            return false;
        }

        var value = reader.GetValue(ordinal);
        return value is bool flag
            ? flag
            : Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
    }

    private static SqlCatalogRoutineKind ParseRoutineKind(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "scalar" => SqlCatalogRoutineKind.Scalar,
            "aggregate" => SqlCatalogRoutineKind.Aggregate,
            "window" => SqlCatalogRoutineKind.Window,
            "table" => SqlCatalogRoutineKind.Table,
            _ => SqlCatalogRoutineKind.Unknown,
        };

    private static SqlCatalogRoutineParameterMode ParseRoutineParameterMode(
        string? value) => value?.Trim().ToLowerInvariant() switch
        {
            "in" => SqlCatalogRoutineParameterMode.In,
            "out" => SqlCatalogRoutineParameterMode.Out,
            "inout" or "in/out" => SqlCatalogRoutineParameterMode.InOut,
            _ => SqlCatalogRoutineParameterMode.Unknown,
        };

    private static SqlCatalogIntrinsicKind? ParseIntrinsicKind(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "keyword" => SqlCatalogIntrinsicKind.Keyword,
            _ => null,
        };

    private static SqlCatalogCoverage DowngradeCatalogCoverage(
        SqlCatalogCoverage coverage) => coverage == SqlCatalogCoverage.None
            ? SqlCatalogCoverage.None
            : SqlCatalogCoverage.Partial;

    private static DatabaseValueKind? RoutineValueKind(string? dataTypeName)
    {
        if (dataTypeName is null)
        {
            return null;
        }

        var kind = DatabaseValueClassifier.Classify(null, dataTypeName);
        return kind == DatabaseValueKind.Other ? null : kind;
    }

    private static int EstimateSqlCatalogRoutineUtf8Bytes(SqlCatalogRoutine routine)
    {
        const int JsonOverheadPerRoutine = 160;
        const int JsonOverheadPerParameter = 96;
        var bytes = JsonOverheadPerRoutine
            + Utf8Length(routine.Id.Catalog)
            + Utf8Length(routine.Id.Schema)
            + Utf8Length(routine.Id.Name)
            + Utf8Length(routine.Signature)
            + Utf8Length(routine.ReturnTypeName);
        foreach (var parameter in routine.Parameters)
        {
            bytes = checked(bytes
                + JsonOverheadPerParameter
                + Utf8Length(parameter.Name)
                + Utf8Length(parameter.DataTypeName));
        }

        return bytes;
    }

    private static string AppendCatalogLimitation(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current) ? addition : $"{current} {addition}";

    private sealed record RoutineKey(
        DatabaseObjectId Id,
        SqlCatalogRoutineKind Kind,
        string Signature,
        string? ReturnTypeName,
        bool HasExplicitArity,
        int? MinimumArgumentCount,
        int? MaximumArgumentCount,
        string? SourceIdentity);

    private sealed record RoutineParameterRow(
        int Ordinal,
        SqlCatalogRoutineParameter Parameter);

    private sealed class RoutineBuilder(RoutineKey key)
    {
        internal RoutineKey Key { get; } = key;

        internal List<RoutineParameterRow> Parameters { get; } = [];
    }

    private sealed record RoutineCatalogReadResult(
        IReadOnlyList<SqlCatalogRoutine> Routines,
        bool IsPartial,
        int EstimatedUtf8Bytes,
        string? Limitation);

    private sealed class SqlCatalogRoutineSemanticComparer :
        IEqualityComparer<SqlCatalogRoutine>
    {
        internal static SqlCatalogRoutineSemanticComparer Instance { get; } = new();

        public bool Equals(SqlCatalogRoutine? left, SqlCatalogRoutine? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            return left is not null
                && right is not null
                && left.Id == right.Id
                && left.Kind == right.Kind
                && string.Equals(left.Signature, right.Signature, StringComparison.Ordinal)
                && string.Equals(
                    left.ReturnTypeName,
                    right.ReturnTypeName,
                    StringComparison.Ordinal)
                && left.ReturnValueKind == right.ReturnValueKind
                && left.MinimumArgumentCount == right.MinimumArgumentCount
                && left.MaximumArgumentCount == right.MaximumArgumentCount
                && left.Parameters.SequenceEqual(right.Parameters);
        }

        public int GetHashCode(SqlCatalogRoutine routine)
        {
            var hash = new HashCode();
            hash.Add(routine.Id);
            hash.Add(routine.Kind);
            hash.Add(routine.Signature, StringComparer.Ordinal);
            hash.Add(routine.ReturnTypeName, StringComparer.Ordinal);
            hash.Add(routine.ReturnValueKind);
            hash.Add(routine.MinimumArgumentCount);
            hash.Add(routine.MaximumArgumentCount);
            foreach (var parameter in routine.Parameters)
            {
                hash.Add(parameter);
            }

            return hash.ToHashCode();
        }
    }

    private sealed record IntrinsicCatalogReadResult(
        IReadOnlyList<SqlCatalogIntrinsicSymbol> Symbols,
        bool IsPartial,
        int EstimatedUtf8Bytes);

    private sealed class IntrinsicSymbolKeyComparer :
        IEqualityComparer<(string Name, SqlCatalogIntrinsicKind Kind)>
    {
        internal static IntrinsicSymbolKeyComparer Instance { get; } = new();

        public bool Equals(
            (string Name, SqlCatalogIntrinsicKind Kind) left,
            (string Name, SqlCatalogIntrinsicKind Kind) right) =>
            left.Kind == right.Kind
            && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, SqlCatalogIntrinsicKind Kind) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name),
                value.Kind);
    }

    private static async Task<IReadOnlyList<DatabaseTableDescriptor>> ReadTablesAsync(
        DbConnection connection,
        IDatabaseDriver driver,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = driver.ListTablesSql;
        var tables = new List<DatabaseTableDescriptor>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(new DatabaseTableDescriptor(
                reader.GetString(2),
                string.Equals(
                    reader.GetString(3).Trim(),
                    "view",
                    StringComparison.OrdinalIgnoreCase)
                    ? DatabaseTableKind.View
                    : DatabaseTableKind.Table,
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return tables;
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken)
    {
        var driver = Resolve(driverId);
        if (ExecutesInWorker)
        {
            return await ExecuteInWorkerAsync(driver, connectionString, tunnel,
                (executor, target, token) => executor.ListDatabasesAsync(target, token),
                cancellationToken).ConfigureAwait(false);
        }

        if (driver.ListDatabasesSql is not { } sql)
        {
            return [];
        }

        return await ExecuteWithConnectionAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (createConnection, token) =>
            {
                await using var connection = createConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                var names = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(token)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0))
                    {
                        names.Add(reader.GetString(0));
                    }
                }

                return (IReadOnlyList<string>)names;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseSessionInfo> DescribeSessionAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken)
    {
        var driver = Resolve(driverId);
        if (ExecutesInWorker)
        {
            return await ExecuteInWorkerAsync(driver, connectionString, tunnel,
                (executor, target, token) => executor.DescribeSessionAsync(target, token),
                cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteWithConnectionAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (createConnection, token) =>
            {
                await using var connection = createConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                return await driver.DescribeSessionAsync(connection, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseQueryPage> QueryAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        CancellationToken cancellationToken) =>
        await QueryCoreAsync(
            driverId,
            connectionString,
            tunnel,
            sql,
            maxRows,
            requestKeyInfo: false,
            cancellationToken).ConfigureAwait(false);

    public async Task<DatabaseQueryPage> QueryWithProvenanceAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        CancellationToken cancellationToken) =>
        await QueryCoreAsync(
            driverId,
            connectionString,
            tunnel,
            sql,
            maxRows,
            // FirebirdSql.Data can leave an otherwise valid result cursor
            // unusable when CommandBehavior.KeyInfo is requested for a
            // projection such as SELECT 1 FROM RDB$DATABASE. Do not retry the
            // statement after that failure: SELECT may invoke user functions,
            // so running it twice is not a safe metadata fallback. Firebird's
            // ordinary column schema remains the conservative provenance
            // source and simply fails closed when the provider omits lineage.
            requestKeyInfo: IsResultQuery(sql)
                && !string.Equals(driverId, "firebird", StringComparison.Ordinal),
            cancellationToken).ConfigureAwait(false);

    private async Task<DatabaseQueryPage> QueryCoreAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sql,
        int maxRows,
        bool requestKeyInfo,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        var driver = Resolve(driverId);
        if (ExecutesInWorker)
        {
            return await ExecuteInWorkerAsync(driver, connectionString, tunnel,
                (executor, target, token) => executor.QueryAsync(target, sql, maxRows, requestKeyInfo, token),
                cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteWithConnectionAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (createConnection, token) =>
            {
                await using var connection = createConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                var result = await ExecuteQueryAsync(
                        connection,
                        new DatabaseSqlCommand(sql, []),
                        maxRows,
                        schema: null,
                        token,
                        requestKeyInfo)
                    .ConfigureAwait(false);
                try
                {
                    if (requestKeyInfo
                        && string.Equals(driverId, "duckdb", StringComparison.Ordinal))
                    {
                        result = await DuckDbQueryProvenance.EnrichAsync(
                                connection,
                                sql,
                                result,
                                token)
                            .ConfigureAwait(false);
                    }

                    return requestKeyInfo
                        ? NormalizeProviderProvenance(driverId, result)
                        : result;
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseObjectDetails> GetObjectDetailsAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor databaseObject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(databaseObject);
        var driver = Resolve(driverId);
        if (ExecutesInWorker)
        {
            return await ExecuteInWorkerAsync(driver, connectionString, tunnel,
                (executor, target, token) => executor.GetObjectDetailsAsync(target, databaseObject, token),
                cancellationToken).ConfigureAwait(false);
        }

        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteWithConnectionAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (createConnection, token) =>
            {
                await using var connection = createConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                return await new DatabaseMetadataReader(dialect)
                    .ReadAsync(connection, databaseObject, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseTablePage> ReadQueryAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        DatabaseTableQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSql);
        ArgumentNullException.ThrowIfNull(sourceColumns);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MaximumTablePageSize);
        var driver = Resolve(driverId);
        if (ExecutesInWorker)
        {
            return await ExecuteInWorkerAsync(driver, connectionString, tunnel,
                (executor, target, token) => executor.ReadQueryAsync(target, sourceSql, sourceColumns, query, token),
                cancellationToken).ConfigureAwait(false);
        }

        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteWithConnectionAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (createConnection, token) =>
            {
                await using var connection = createConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                var requestedLimit = query.Limit;
                var readQuery = query with { Limit = requestedLimit + 1 };
                var projectedColumns = dialect.ProjectColumns(sourceColumns, query);
                var command = dialect.BuildQuerySelect(sourceSql, sourceColumns, readQuery);
                var result = await ExecuteQueryAsync(
                        connection,
                        command,
                        readQuery.Limit,
                        schema: null,
                        token)
                    .ConfigureAwait(false);
                try
                {
                    result = PreserveQueryColumnContext(result, projectedColumns);
                    var hasLookAheadRow = result.ValueRows.Count > requestedLimit;
                    var pageResult = hasLookAheadRow
                        ? result with
                        {
                            Rows = [.. result.Rows.Take(requestedLimit)],
                            TypedRows = [.. result.ValueRows.Take(requestedLimit)],
                            Truncated = true,
                        }
                        : result;
                    var filteredRows = await ExecuteCountAsync(
                            connection,
                            dialect.BuildQueryCount(sourceSql, sourceColumns, query.Filters),
                            token)
                        .ConfigureAwait(false);
                    var sourceRows = query.Filters.Count == 0
                        ? filteredRows
                        : await ExecuteCountAsync(
                                connection,
                                dialect.BuildQueryCount(sourceSql, sourceColumns, []),
                                token)
                            .ConfigureAwait(false);
                    return new DatabaseTablePage(
                        pageResult,
                        query.Offset,
                        requestedLimit,
                        hasLookAheadRow,
                        filteredRows,
                        sourceRows);
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> CountQueryRowsAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        string sourceSql,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns,
        IReadOnlyList<DatabaseFilterCondition> filters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSql);
        ArgumentNullException.ThrowIfNull(sourceColumns);
        ArgumentNullException.ThrowIfNull(filters);
        var driver = Resolve(driverId);
        if (ExecutesInWorker)
        {
            return await ExecuteInWorkerAsync(driver, connectionString, tunnel,
                (executor, target, token) => executor.CountQueryRowsAsync(target, sourceSql, sourceColumns, filters, token),
                cancellationToken).ConfigureAwait(false);
        }

        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteWithConnectionAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (createConnection, token) =>
            {
                await using var connection = createConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                return await ExecuteCountAsync(
                        connection,
                        dialect.BuildQueryCount(sourceSql, sourceColumns, filters),
                        token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseTablePage> ReadTableAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor table,
        DatabaseTableQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MaximumTablePageSize);
        var driver = Resolve(driverId);
        if (ExecutesInWorker)
        {
            return await ExecuteInWorkerAsync(driver, connectionString, tunnel,
                (executor, target, token) => executor.ReadTableAsync(target, table, query, token),
                cancellationToken).ConfigureAwait(false);
        }

        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteWithConnectionAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (createConnection, token) =>
            {
                await using var connection = createConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                var details = await new DatabaseMetadataReader(dialect)
                    .ReadAsync(connection, table, token, includeIndexes: false)
                    .ConfigureAwait(false);
                var canPage = details.PrimaryKey.Count > 0;
                if (!canPage && query.Offset > 0)
                {
                    throw new InvalidOperationException(
                        "This object has no primary key, so only its first page can be read safely.");
                }

                var requestedLimit = query.Limit;
                var readQuery = query with
                {
                    Limit = query.Limit + 1,
                };
                var projectedColumns = dialect.ProjectColumns(details.Columns, query);
                var command = dialect.BuildSelect(table.Id, details.Columns, readQuery);
                var result = await ExecuteQueryAsync(
                        connection,
                        command,
                        readQuery.Limit,
                        projectedColumns,
                        token)
                    .ConfigureAwait(false);
                try
                {
                    var hasLookAheadRow = result.ValueRows.Count > requestedLimit;
                    var pageResult = hasLookAheadRow
                        ? result with
                        {
                            Rows = [.. result.Rows.Take(requestedLimit)],
                            TypedRows = [.. result.ValueRows.Take(requestedLimit)],
                            Truncated = true,
                        }
                        : result;
                    var filteredRows = await ExecuteCountAsync(
                            connection,
                            dialect.BuildCount(table.Id, details.Columns, query.Filters),
                            token)
                        .ConfigureAwait(false);
                    var tableRows = query.Filters.Count == 0
                        ? filteredRows
                        : await ExecuteCountAsync(
                                connection,
                                dialect.BuildCount(table.Id, details.Columns, []),
                                token)
                            .ConfigureAwait(false);
                    return new DatabaseTablePage(
                        pageResult,
                        query.Offset,
                        requestedLimit,
                        hasLookAheadRow && canPage,
                        filteredRows,
                        tableRows);
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatabaseMutationResult> ApplyTableChangesAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        DatabaseTableDescriptor table,
        DatabaseTableChanges changes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.IsEmpty)
        {
            return new DatabaseMutationResult(0, 0, 0);
        }

        var driver = Resolve(driverId);
        if (ExecutesInWorker)
        {
            return await ExecuteInWorkerAsync(driver, connectionString, tunnel,
                (executor, target, token) => executor.ApplyTableChangesAsync(target, table, changes, token),
                cancellationToken).ConfigureAwait(false);
        }

        var dialect = DatabaseSqlDialect.For(driverId);
        return await ExecuteWithConnectionAsync(
            driver,
            driver.NormalizeConnectionString(connectionString),
            tunnel,
            async (createConnection, token) =>
            {
                await using var connection = createConnection();
                await connection.OpenAsync(token).ConfigureAwait(false);
                var details = await new DatabaseMetadataReader(dialect)
                    .ReadAsync(connection, table, token, includeIndexes: false)
                    .ConfigureAwait(false);
                if (!details.CanEdit)
                {
                    throw new InvalidOperationException(details.ReadOnlyReason ?? "This table is read-only.");
                }

                return await ApplyChangesAsync(connection, dialect, details, changes, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public string BuildTablePreviewQuery(string driverId, string tableName, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return Resolve(driverId).BuildPreviewQuery(tableName, limit);
    }

    public string BuildInsertStatement(
        string driverId,
        DatabaseObjectDetails details,
        DatabaseInsertedRow row,
        int maximumUtf8Bytes = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(row);
        if (details.Object.Kind != DatabaseTableKind.Table)
        {
            throw new InvalidOperationException("INSERT scripts require a physical table.");
        }

        return DatabaseSqlDialect.For(driverId)
            .BuildInsertStatement(details.Object.Id, details, row, maximumUtf8Bytes);
    }

    public string BuildTablePreviewQuery(string driverId, DatabaseObjectId table, int limit)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return DatabaseSqlDialect.For(driverId)
            .BuildSelect(table, [], DatabaseTableQuery.FirstPage(limit))
            .Sql;
    }

    public DatabaseConnectionDetails ParseConnectionDetails(
        string driverId,
        string connectionString) =>
        ResolveDetails(Resolve(driverId), connectionString ?? string.Empty);

    public bool IsConnectionStringValid(string driverId, string connectionString)
    {
        try
        {
            var driver = Resolve(driverId);
            // Provider constructors parse supported options. Do not use the
            // editor's permissive fallback, Open, or a routed connection here.
            using var connection = driver.CreateConnection(driver.NormalizeConnectionString(connectionString));
            return connection.State == ConnectionState.Closed;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException
            or NotSupportedException or InvalidOperationException or DbException)
        {
            return false;
        }
    }

    /// <summary>
    /// A URL pasted into the connection box fills the host, port, database and
    /// credential fields too, rather than sitting there as one opaque line —
    /// so the engines that speak one are asked to translate before the fields
    /// are read out of it.
    /// </summary>
    private static DatabaseConnectionDetails ResolveDetails(
        IDatabaseDriver driver,
        string connectionString)
    {
        try
        {
            return driver.ParseDetails(driver.NormalizeConnectionString(connectionString));
        }
        catch (ArgumentException)
        {
            // A URL naming something this build cannot honour is still worth
            // showing as it was typed; the refusal is raised when it is used.
            return driver.ParseDetails(connectionString);
        }
    }

    public string BuildConnectionString(string driverId, DatabaseConnectionDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return Resolve(driverId).BuildConnectionString(details);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task<TResult> ExecuteInWorkerAsync<TResult>(
        IDatabaseDriver driver,
        string connectionString,
        ConnectionProfile? tunnel,
        Func<IDatabaseOperationExecutor, DatabaseWorkerConnection, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        var normalized = driver.NormalizeConnectionString(connectionString);
        var selectedRoute = tunnel ?? _defaultTunnel;
        if (_operationExecutorSelector is { } selectExecutor)
        {
            var executor = await selectExecutor(driver.Descriptor, selectedRoute, cancellationToken).ConfigureAwait(false);
            return await operation(executor, new DatabaseWorkerConnection(driver.Descriptor.Id, normalized), cancellationToken).ConfigureAwait(false);
        }
        RejectUnselectedSshRoute(selectedRoute);
        return await operation(_operationExecutor!, new DatabaseWorkerConnection(driver.Descriptor.Id, normalized), cancellationToken).ConfigureAwait(false);
    }

    private Task<TResult> ExecuteWithConnectionAsync<TResult>(
        IDatabaseDriver driver,
        string connectionString,
        ConnectionProfile? tunnel,
        Func<Func<DbConnection>, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        RejectUnselectedSshRoute(tunnel ?? _defaultTunnel);
        return operation(() => driver.CreateConnection(connectionString), cancellationToken);
    }

    private static void RejectUnselectedSshRoute(ConnectionProfile? route)
    {
        if (route?.Endpoint is ConnectionEndpoint.Ssh)
        {
            throw new InvalidOperationException("SSH database operations require the selected connection backend.");
        }
    }

    private IDatabaseDriver Resolve(string driverId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(driverId);
        return _drivers.TryGetValue(driverId, out var driver)
            ? driver
            : throw new ArgumentException(
                $"The database driver '{driverId}' is not available in this build.",
                nameof(driverId));
    }

    private async Task<DatabaseQueryPage> ExecuteQueryAsync(
        DbConnection connection,
        DatabaseSqlCommand statement,
        int maxRows,
        IReadOnlyList<DatabaseColumnSchema>? schema,
        CancellationToken cancellationToken,
        bool requestKeyInfo = false)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var command = CreateCommand(connection, statement);
        var behavior = requestKeyInfo ? CommandBehavior.KeyInfo : CommandBehavior.Default;
        if (_contentStoreFactory is not null)
        {
            behavior |= CommandBehavior.SequentialAccess;
        }
        await using var reader = await command.ExecuteReaderAsync(behavior, cancellationToken)
            .ConfigureAwait(false);
        var described = DatabaseValueMaterializer.DescribeColumns(reader);
        var visibleOrdinals = described
            .Select((column, ordinal) => (column, ordinal))
            .Where(item => !item.column.IsHidden)
            .ToArray();
        var columns = schema is null
            ? visibleOrdinals.Select(item => item.column).ToArray()
            : [.. visibleOrdinals.Select(item => MergeSchema(item.column, schema))];
        var values = new List<IReadOnlyList<DatabaseValue>>();
        var displayRows = new List<IReadOnlyList<string?>>();
        var truncated = false;
        DatabaseValueContentStore? contentStore = null;
        var completed = false;
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (values.Count >= maxRows)
                {
                    truncated = true;
                    break;
                }

                var typedRow = new DatabaseValue[columns.Length];
                var displayRow = new string?[columns.Length];
                for (var ordinal = 0; ordinal < columns.Length; ordinal++)
                {
                    var readerOrdinal = visibleOrdinals[ordinal].ordinal;
                    var value = _contentStoreFactory is null
                        ? DatabaseValueMaterializer.Materialize(reader, readerOrdinal, columns[ordinal])
                        : await DatabaseValueMaterializer.MaterializeAsync(
                            reader,
                            readerOrdinal,
                            columns[ordinal],
                            () => contentStore ??= _contentStoreFactory(),
                            cancellationToken).ConfigureAwait(false);
                    typedRow[ordinal] = value;
                    displayRow[ordinal] = value.IsNull ? null : value.DisplayText;
                }

                values.Add(typedRow);
                displayRows.Add(displayRow);
            }

            stopwatch.Stop();
            var safeColumns = DatabaseValueMaterializer.ReconcileColumnSafety(columns, values);
            completed = true;
            return new DatabaseQueryPage(
                safeColumns,
                displayRows,
                truncated,
                Math.Max(0, reader.RecordsAffected),
                stopwatch.Elapsed,
                values,
                contentStore);
        }
        finally
        {
            if (!completed)
            {
                contentStore?.Dispose();
            }
        }
    }

    private static bool IsResultQuery(string sql)
    {
        var statement = sql.AsSpan().TrimStart();
        return statement.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || statement.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }

    private static DatabaseColumnDescriptor MergeSchema(
        DatabaseColumnDescriptor column,
        IReadOnlyList<DatabaseColumnSchema> schema)
    {
        var metadata = schema.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, column.Name, StringComparison.Ordinal));
        return metadata is null
            ? column
            : column with
            {
                DataTypeName = metadata.DataTypeName,
                ValueKind = metadata.ValueKind == DatabaseValueKind.Other
                    ? column.ValueKind
                    : metadata.ValueKind,
                IsNullable = metadata.IsNullable,
                IsKey = metadata.IsPrimaryKey,
                IsIdentity = metadata.IsIdentity,
                IsReadOnly = !metadata.CanEdit,
                BaseColumnName = metadata.Name,
                DefaultExpression = metadata.DefaultExpression,
            };
    }

    private static DatabaseQueryPage PreserveQueryColumnContext(
        DatabaseQueryPage result,
        IReadOnlyList<DatabaseColumnDescriptor> sourceColumns)
    {
        if (result.Columns.Count != sourceColumns.Count)
        {
            return result;
        }

        var columns = result.Columns
            .Select((column, ordinal) =>
            {
                var source = sourceColumns[ordinal];
                return string.Equals(column.Name, source.Name, StringComparison.Ordinal)
                    ? column with
                    {
                        IsNullable = source.IsNullable,
                        IsKey = source.IsKey,
                        IsIdentity = source.IsIdentity,
                        // Several providers describe every column projected by
                        // our derived-table wrapper as read-only even when the
                        // proven source column is writable. The wrapper is a
                        // browsing implementation detail; mutation safety comes
                        // from the exact source provenance and table metadata.
                        // Provider-owned values still fail closed later through
                        // ReconcileColumnSafety/HasDisplayOnlyValue.
                        IsReadOnly = source.IsReadOnly,
                        BaseColumnName = source.BaseColumnName,
                        DefaultExpression = source.DefaultExpression,
                        BaseObject = source.BaseObject,
                    }
                    : column;
            })
            .ToArray();
        return result with { Columns = columns };
    }

    private static DatabaseQueryPage NormalizeProviderProvenance(
        string driverId,
        DatabaseQueryPage result)
    {
        if (!string.Equals(driverId, "sqlite", StringComparison.Ordinal))
        {
            return result;
        }

        var columns = result.Columns
            .Select(column => column.BaseObject is { } source
                    && string.Equals(source.Catalog, "main", StringComparison.OrdinalIgnoreCase)
                ? column with { BaseObject = source with { Catalog = null } }
                : column)
            .ToArray();
        return result with { Columns = columns };
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DatabaseSqlCommand statement,
        DbTransaction? transaction = null)
    {
        if (statement.Parameters.Any(parameter => parameter.Value is DatabaseValueContent))
        {
            throw new InvalidOperationException("Detached database parameters must be decoded by the owned operation worker before provider dispatch.");
        }

        var command = connection.CreateCommand();
        command.CommandText = statement.Sql;
        command.Transaction = transaction;
        foreach (var value in statement.Parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = value.Name;
            parameter.Value = value.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private static async Task<long> ExecuteCountAsync(
        DbConnection connection,
        DatabaseSqlCommand statement,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, statement);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            throw new InvalidOperationException("The database did not return a row count.");
        }

        long count;
        try
        {
            count = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException
            or InvalidCastException
            or OverflowException)
        {
            throw new InvalidOperationException(
                "The database returned an invalid row count.",
                exception);
        }

        return count >= 0
            ? count
            : throw new InvalidOperationException("The database returned a negative row count.");
    }

    private static async Task<DatabaseMutationResult> ApplyChangesAsync(
        DbConnection connection,
        DatabaseSqlDialect dialect,
        DatabaseObjectDetails details,
        DatabaseTableChanges changes,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var inserted = 0;
        var updated = 0;
        var deleted = 0;
        try
        {
            foreach (var row in changes.Inserts)
            {
                inserted += await ExecuteMutationAsync(
                        connection,
                        transaction,
                        dialect.BuildInsert(details.Object.Id, details, row),
                        expectSingleRow: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var row in changes.Updates)
            {
                var affected = await ExecuteMutationAsync(
                        connection,
                        transaction,
                        dialect.BuildUpdate(details.Object.Id, details, row),
                        expectSingleRow: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (affected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new DatabaseMutationResult(0, 0, 0, true, "The row changed since it was loaded.");
                }

                updated += affected;
            }

            foreach (var row in changes.Deletes)
            {
                var affected = await ExecuteMutationAsync(
                        connection,
                        transaction,
                        dialect.BuildDelete(details.Object.Id, details, row),
                        expectSingleRow: false,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (affected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return new DatabaseMutationResult(0, 0, 0, true, "The row changed since it was loaded.");
                }

                deleted += affected;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DatabaseMutationResult(inserted, updated, deleted);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> ExecuteMutationAsync(
        DbConnection connection,
        DbTransaction transaction,
        DatabaseSqlCommand statement,
        bool expectSingleRow,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, statement, transaction);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (affected < 0 || affected > 1 || (expectSingleRow && affected != 1))
        {
            throw new InvalidOperationException(
                $"A row mutation affected {affected} rows; exactly one was expected.");
        }

        return affected;
    }
}
