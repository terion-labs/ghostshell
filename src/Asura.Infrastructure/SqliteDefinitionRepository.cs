using System.Globalization;
using System.Text.Json;
using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

public sealed class SqliteDefinitionRepository<TDefinition> : IDefinitionRepository<TDefinition>
    where TDefinition : IDurableDefinition
{
    private readonly AsuraDatabase _database;
    private readonly TimeProvider _timeProvider;

    public SqliteDefinitionRepository(AsuraDatabase database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _timeProvider = timeProvider;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> GetAsync(
        DefinitionKey key,
        CancellationToken cancellationToken)
    {
        if (!KnownDefinitionRegistry.SupportsRepositoryType<TDefinition>()
            || key.Kind != TDefinition.Kind)
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.UnsupportedKind,
                "The requested definition kind does not match this repository.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT kind, id, schema_version, revision, name, payload_json, created_utc, updated_utc
                FROM definitions
                WHERE kind = $kind AND id = $id;
                """;
            command.Parameters.AddWithValue("$kind", TDefinition.Kind.Value);
            command.Parameters.AddWithValue("$id", key.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return Failure<StoredDefinition<TDefinition>>(
                    DefinitionStoreErrorCode.NotFound,
                    "The requested definition does not exist.");
            }

            return DeserializeStored(reader);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.Cancelled,
                "The definition read was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<StoredDefinition<TDefinition>>(
                MapSqliteError(exception),
                "The definition store could not complete the read.");
        }
        catch (Exception exception) when (IsStorageFormatException(exception))
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.StorageFailure,
                "The stored definition has corrupt metadata.");
        }
    }

    public async ValueTask<DefinitionStoreResult<IReadOnlyList<StoredDefinition<TDefinition>>>> ListAsync(
        CancellationToken cancellationToken)
    {
        if (!KnownDefinitionRegistry.SupportsRepositoryType<TDefinition>())
        {
            return Failure<IReadOnlyList<StoredDefinition<TDefinition>>>(
                DefinitionStoreErrorCode.UnsupportedKind,
                "This repository type is not a supported durable definition.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT kind, id, schema_version, revision, name, payload_json, created_utc, updated_utc
                FROM definitions
                WHERE kind = $kind
                ORDER BY name COLLATE NOCASE, id;
                """;
            command.Parameters.AddWithValue("$kind", TDefinition.Kind.Value);
            var definitions = new List<StoredDefinition<TDefinition>>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var item = DeserializeStored(reader);
                    if (item.IsSuccess)
                    {
                        definitions.Add(item.Value!);
                        continue;
                    }

                    // Released payload versions migrate before repositories open.
                    // Anything unreadable here is corruption or a version this
                    // binary cannot safely interpret. Preserve the row and fail
                    // instead of turning a read into irreversible data loss.
                    return Failure<IReadOnlyList<StoredDefinition<TDefinition>>>(
                        item.Error!.Code,
                        item.Error.Message);
                }
            }

            return DefinitionStoreResult<IReadOnlyList<StoredDefinition<TDefinition>>>.Success(
                definitions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<IReadOnlyList<StoredDefinition<TDefinition>>>(
                DefinitionStoreErrorCode.Cancelled,
                "The definition list was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<IReadOnlyList<StoredDefinition<TDefinition>>>(
                MapSqliteError(exception),
                "The definition store could not complete the list operation.");
        }
        catch (Exception exception) when (IsStorageFormatException(exception))
        {
            return Failure<IReadOnlyList<StoredDefinition<TDefinition>>>(
                DefinitionStoreErrorCode.StorageFailure,
                "A stored definition has corrupt metadata.");
        }
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<TDefinition>>> SaveAsync(
        TDefinition definition,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!KnownDefinitionRegistry.SupportsRepositoryType<TDefinition>())
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.UnsupportedKind,
                "This repository type is not a supported durable definition.");
        }

        var definitionProblem = KnownDefinitionRegistry.ValidateForSave(definition);
        if (definitionProblem is not null)
        {
            return FromProblem<StoredDefinition<TDefinition>>(definitionProblem);
        }

        var definitionKey = definition.Key;
        var definitionSchemaVersion = definition.SchemaVersion;
        var definitionName = definition.Name;
        if (definitionKey.Kind != TDefinition.Kind)
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.UnsupportedKind,
                "The definition kind does not match this repository.");
        }

        string payloadJson;
        try
        {
            payloadJson = DefinitionJson.Serialize(definition);
        }
        catch (Exception exception) when (IsPayloadException(exception))
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.InvalidDefinition,
                "The definition cannot be serialized by this application version.");
        }

        if (!PortablePayloadSafety.TryValidate(payloadJson, out var safetyError))
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.UnsafePayload,
                safetyError!);
        }

        var document = new PortableDefinitionDocument(
            definitionKey.Kind,
            definitionKey.Value,
            definitionSchemaVersion,
            definitionName,
            payloadJson);
        if (!KnownDefinitionRegistry.TryParse(
                document,
                out var parsed,
                out var snapshotProblem))
        {
            return FromProblem<StoredDefinition<TDefinition>>(snapshotProblem!);
        }

        if (parsed is not TDefinition persistedDefinition)
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.UnsupportedKind,
                "The serialized definition does not match this repository type.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var prospective = new Dictionary<DefinitionKey, object>
                {
                    [persistedDefinition.Key] = persistedDefinition,
                };
                var validator = new SqliteDefinitionGraphValidator(
                    connection,
                    transaction,
                    prospective);
                var validationProblems = await validator.ValidateBatchAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (validationProblems.Count > 0)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return FromProblem<StoredDefinition<TDefinition>>(validationProblems[0]);
                }

                var result = expectedRevision is null
                    ? await InsertAsync(
                            connection,
                            transaction,
                            persistedDefinition,
                            payloadJson,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await UpdateAsync(
                            connection,
                            transaction,
                            persistedDefinition,
                            payloadJson,
                            expectedRevision.Value,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return result;
                }

                await ReplaceReferencesAsync(
                        connection,
                        transaction,
                        persistedDefinition.Key,
                        DefinitionReferenceExtractor.Extract(persistedDefinition),
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.Cancelled,
                "The definition save was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<StoredDefinition<TDefinition>>(
                MapSqliteError(exception),
                "The definition store could not complete the save.");
        }
        catch (Exception exception) when (IsStorageFormatException(exception))
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.StorageFailure,
                "Stored definition metadata is corrupt.");
        }
    }

    public async ValueTask<DefinitionStoreResult<Unit>> DeleteAsync(
        DefinitionKey key,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (!KnownDefinitionRegistry.SupportsRepositoryType<TDefinition>()
            || key.Kind != TDefinition.Kind)
        {
            return Failure<Unit>(
                DefinitionStoreErrorCode.UnsupportedKind,
                "The definition kind does not match this repository.");
        }

        if (expectedRevision <= 0)
        {
            return Failure<Unit>(
                DefinitionStoreErrorCode.InvalidDefinition,
                "A positive expected revision is required to delete a definition.");
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var dependency = await ReadFirstInboundReferenceAsync(
                        connection,
                        transaction,
                        key,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (dependency is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return Failure<Unit>(
                        DefinitionStoreErrorCode.DependencyConflict,
                        $"Definition '{key}' is still referenced by '{dependency.Value}'.");
                }

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    DELETE FROM definitions
                    WHERE kind = $kind AND id = $id AND revision = $expectedRevision;
                    """;
                command.Parameters.AddWithValue("$kind", TDefinition.Kind.Value);
                command.Parameters.AddWithValue("$id", key.Value);
                command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (affected == 1)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return DefinitionStoreResult<Unit>.Success(Unit.Value);
                }

                var currentRevision = await ReadRevisionAsync(
                        connection,
                        transaction,
                        key,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return currentRevision is null
                    ? Failure<Unit>(
                        DefinitionStoreErrorCode.NotFound,
                        "The definition does not exist.")
                    : DefinitionStoreResult<Unit>.Failure(
                        new DefinitionStoreError(
                            DefinitionStoreErrorCode.RevisionConflict,
                            "The definition changed before it could be deleted.",
                            currentRevision));
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<Unit>(
                DefinitionStoreErrorCode.Cancelled,
                "The definition delete was cancelled.");
        }
        catch (SqliteException exception)
        {
            return Failure<Unit>(
                MapSqliteError(exception),
                "The definition store could not complete the delete.");
        }
        catch (Exception exception) when (IsStorageFormatException(exception))
        {
            return Failure<Unit>(
                DefinitionStoreErrorCode.StorageFailure,
                "Stored dependency metadata is corrupt.");
        }
    }

    private async Task<DefinitionStoreResult<StoredDefinition<TDefinition>>> InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TDefinition definition,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO definitions(
                kind, id, schema_version, revision, name, payload_json, created_utc, updated_utc)
            VALUES ($kind, $id, $schemaVersion, 1, $name, $payloadJson, $now, $now)
            ON CONFLICT(kind, id) DO NOTHING
            RETURNING revision, created_utc, updated_utc;
            """;
        AddDefinitionParameters(command, definition, payloadJson);
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        long? insertedRevision = null;
        DateTimeOffset insertedCreatedAt = default;
        DateTimeOffset insertedUpdatedAt = default;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                insertedRevision = reader.GetInt64(0);
                insertedCreatedAt = ParseTimestamp(reader.GetString(1));
                insertedUpdatedAt = ParseTimestamp(reader.GetString(2));
            }
        }

        if (insertedRevision is not null)
        {
            return DefinitionStoreResult<StoredDefinition<TDefinition>>.Success(
                new StoredDefinition<TDefinition>(
                    definition,
                    insertedRevision.Value,
                    insertedCreatedAt,
                    insertedUpdatedAt));
        }

        var currentRevision = await ReadRevisionAsync(
                connection,
                transaction,
                definition.Key,
                cancellationToken)
            .ConfigureAwait(false);
        return DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(
            new DefinitionStoreError(
                DefinitionStoreErrorCode.RevisionConflict,
                "A definition with this identity already exists.",
                currentRevision));
    }

    private async Task<DefinitionStoreResult<StoredDefinition<TDefinition>>> UpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TDefinition definition,
        string payloadJson,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (expectedRevision <= 0)
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.InvalidDefinition,
                "A positive expected revision is required to update a definition.");
        }

        var now = _timeProvider.GetUtcNow();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE definitions
            SET schema_version = $schemaVersion,
                revision = revision + 1,
                name = $name,
                payload_json = $payloadJson,
                updated_utc = $now
            WHERE kind = $kind AND id = $id AND revision = $expectedRevision
            RETURNING revision, created_utc, updated_utc;
            """;
        AddDefinitionParameters(command, definition, payloadJson);
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        long? updatedRevision = null;
        DateTimeOffset updatedCreatedAt = default;
        DateTimeOffset updatedUpdatedAt = default;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                updatedRevision = reader.GetInt64(0);
                updatedCreatedAt = ParseTimestamp(reader.GetString(1));
                updatedUpdatedAt = ParseTimestamp(reader.GetString(2));
            }
        }

        if (updatedRevision is not null)
        {
            return DefinitionStoreResult<StoredDefinition<TDefinition>>.Success(
                new StoredDefinition<TDefinition>(
                    definition,
                    updatedRevision.Value,
                    updatedCreatedAt,
                    updatedUpdatedAt));
        }

        var currentRevision = await ReadRevisionAsync(
                connection,
                transaction,
                definition.Key,
                cancellationToken)
            .ConfigureAwait(false);
        return currentRevision is null
            ? Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.NotFound,
                "The definition does not exist.")
            : DefinitionStoreResult<StoredDefinition<TDefinition>>.Failure(
                new DefinitionStoreError(
                    DefinitionStoreErrorCode.RevisionConflict,
                    "The definition changed before it could be saved.",
                    currentRevision));
    }

    private static void AddDefinitionParameters(
        SqliteCommand command,
        TDefinition definition,
        string payloadJson)
    {
        command.Parameters.AddWithValue("$kind", TDefinition.Kind.Value);
        command.Parameters.AddWithValue("$id", definition.Key.Value);
        command.Parameters.AddWithValue("$schemaVersion", definition.SchemaVersion);
        command.Parameters.AddWithValue("$name", definition.Name);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
    }

    private static async Task<long?> ReadRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DefinitionKey key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision FROM definitions WHERE kind = $kind AND id = $id;
            """;
        command.Parameters.AddWithValue("$kind", key.Kind.Value);
        command.Parameters.AddWithValue("$id", key.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null || value is DBNull
            ? null
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<DefinitionKey?> ReadFirstInboundReferenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DefinitionKey target,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT owner_kind, owner_id
            FROM definition_references
            WHERE target_kind = $targetKind AND target_id = $targetId
            ORDER BY owner_kind, owner_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$targetKind", target.Kind.Value);
        command.Parameters.AddWithValue("$targetId", target.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new DefinitionKey(new DefinitionKind(reader.GetString(0)), reader.GetString(1))
            : null;
    }

    private static async Task ReplaceReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DefinitionKey owner,
        IReadOnlyList<DefinitionReference> references,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM definition_references
                WHERE owner_kind = $ownerKind AND owner_id = $ownerId;
                """;
            delete.Parameters.AddWithValue("$ownerKind", owner.Kind.Value);
            delete.Parameters.AddWithValue("$ownerId", owner.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var reference in references.Distinct())
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO definition_references(
                    owner_kind, owner_id, target_kind, target_id, role)
                VALUES ($ownerKind, $ownerId, $targetKind, $targetId, $role);
                """;
            insert.Parameters.AddWithValue("$ownerKind", owner.Kind.Value);
            insert.Parameters.AddWithValue("$ownerId", owner.Value);
            insert.Parameters.AddWithValue("$targetKind", reference.Target.Kind.Value);
            insert.Parameters.AddWithValue("$targetId", reference.Target.Value);
            insert.Parameters.AddWithValue("$role", reference.Role);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static DefinitionStoreResult<StoredDefinition<TDefinition>> DeserializeStored(
        SqliteDataReader reader)
    {
        try
        {
            var document = new PortableDefinitionDocument(
                new DefinitionKind(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(4),
                reader.GetString(5));
            if (!KnownDefinitionRegistry.TryParse(
                    document,
                    out var parsed,
                    out var problem))
            {
                return FromProblem<StoredDefinition<TDefinition>>(problem!);
            }

            if (parsed is not TDefinition definition)
            {
                return Failure<StoredDefinition<TDefinition>>(
                    DefinitionStoreErrorCode.UnsupportedKind,
                    "The stored definition does not match this repository type.");
            }

            if (!TryParseTimestamp(reader.GetString(6), out var createdAt)
                || !TryParseTimestamp(reader.GetString(7), out var updatedAt))
            {
                return Failure<StoredDefinition<TDefinition>>(
                    DefinitionStoreErrorCode.StorageFailure,
                    "The stored definition has invalid timestamps.");
            }

            return DefinitionStoreResult<StoredDefinition<TDefinition>>.Success(
                new StoredDefinition<TDefinition>(
                    definition,
                    reader.GetInt64(3),
                    createdAt,
                    updatedAt));
        }
        catch (Exception exception) when (IsStorageFormatException(exception))
        {
            return Failure<StoredDefinition<TDefinition>>(
                DefinitionStoreErrorCode.StorageFailure,
                "The stored definition has corrupt metadata.");
        }
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);

    private static DateTimeOffset ParseTimestamp(string value) =>
        TryParseTimestamp(value, out var timestamp)
            ? timestamp
            : throw new FormatException("The stored definition timestamp is invalid.");

    private static DefinitionStoreResult<T> FromProblem<T>(DefinitionProblem problem) =>
        Failure<T>(
            problem.Kind switch
            {
                DefinitionProblemKind.InvalidDefinition => DefinitionStoreErrorCode.InvalidDefinition,
                DefinitionProblemKind.UnsupportedKind => DefinitionStoreErrorCode.UnsupportedKind,
                DefinitionProblemKind.UnsupportedSchema => DefinitionStoreErrorCode.UnsupportedSchema,
                DefinitionProblemKind.UnsafePayload => DefinitionStoreErrorCode.UnsafePayload,
                DefinitionProblemKind.MissingDependency or DefinitionProblemKind.DependencyConflict =>
                    DefinitionStoreErrorCode.DependencyConflict,
                _ => DefinitionStoreErrorCode.StorageFailure,
            },
            problem.Message);

    private static DefinitionStoreResult<T> Failure<T>(
        DefinitionStoreErrorCode code,
        string message) =>
        DefinitionStoreResult<T>.Failure(new DefinitionStoreError(code, message));

    private static DefinitionStoreErrorCode MapSqliteError(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6
            ? DefinitionStoreErrorCode.StorageUnavailable
            : DefinitionStoreErrorCode.StorageFailure;

    private static bool IsPayloadException(Exception exception) =>
        exception is JsonException
            or NotSupportedException
            or ArgumentException
            or InvalidOperationException;

    private static bool IsStorageFormatException(Exception exception) =>
        exception is JsonException
            or NotSupportedException
            or ArgumentException
            or InvalidOperationException
            or InvalidCastException
            or FormatException
            or OverflowException;
}
