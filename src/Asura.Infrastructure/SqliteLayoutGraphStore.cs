using System.Globalization;
using System.Text.Json;
using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure;

/// <summary>
/// Writes a layout and its reconciled dependent screens as one transaction and
/// one prospective validation batch, the same way a bundle import commits a
/// layout with its screens. The per-definition repository cannot express this:
/// its single-item batch makes the graph validator reject the layout for the
/// screens' sake, and would reject the screens for the layout's sake.
/// </summary>
public sealed class SqliteLayoutGraphStore : ILayoutGraphStore
{
    private readonly AsuraDatabase _database;
    private readonly TimeProvider _timeProvider;

    public SqliteLayoutGraphStore(AsuraDatabase database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _timeProvider = timeProvider;
    }

    public async ValueTask<DefinitionStoreResult<StoredDefinition<LayoutDefinition>>>
        SaveLayoutWithScreensAsync(
            LayoutDefinition layout,
            long? expectedLayoutRevision,
            IReadOnlyList<ScreenRevisionUpdate> screens,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(screens);

        if (Prepare(layout) is not { } layoutWrite)
        {
            return Failure(
                DefinitionStoreErrorCode.InvalidDefinition,
                "The layout cannot be serialized by this application version.");
        }

        var screenWrites = new List<(PendingWrite Write, long ExpectedRevision)>(screens.Count);
        foreach (var screen in screens)
        {
            if (Prepare(screen.Definition) is not { } screenWrite)
            {
                return Failure(
                    DefinitionStoreErrorCode.InvalidDefinition,
                    "A reconciled screen cannot be serialized by this application version.");
            }

            screenWrites.Add((screenWrite, screen.ExpectedRevision));
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
                    [layoutWrite.Definition.Key] = layoutWrite.Definition,
                };
                foreach (var (write, _) in screenWrites)
                {
                    prospective[write.Definition.Key] = write.Definition;
                }

                var validator = new SqliteDefinitionGraphValidator(
                    connection,
                    transaction,
                    prospective);
                var problems = await validator.ValidateBatchAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (problems.Count > 0)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    var problem = problems[0];
                    return Failure(
                        problem.Kind switch
                        {
                            DefinitionProblemKind.DependencyConflict =>
                                DefinitionStoreErrorCode.DependencyConflict,
                            DefinitionProblemKind.StorageFailure =>
                                DefinitionStoreErrorCode.StorageFailure,
                            _ => DefinitionStoreErrorCode.InvalidDefinition,
                        },
                        problem.Message);
                }

                var layoutResult = await WriteAsync(
                        connection,
                        transaction,
                        layoutWrite,
                        expectedLayoutRevision,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (layoutResult.Revision is null)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return Failure(layoutResult.ErrorCode, layoutResult.ErrorMessage);
                }

                foreach (var (write, expectedRevision) in screenWrites)
                {
                    var screenResult = await WriteAsync(
                            connection,
                            transaction,
                            write,
                            expectedRevision,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (screenResult.Revision is null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        return Failure(screenResult.ErrorCode, screenResult.ErrorMessage);
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DefinitionStoreResult<StoredDefinition<LayoutDefinition>>.Success(
                    new StoredDefinition<LayoutDefinition>(
                        (LayoutDefinition)layoutWrite.Definition,
                        layoutResult.Revision.Value,
                        layoutResult.CreatedAt,
                        layoutResult.UpdatedAt));
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                DefinitionStoreErrorCode.Cancelled,
                "The layout save was cancelled and rolled back.");
        }
        catch (SqliteException)
        {
            return Failure(
                DefinitionStoreErrorCode.StorageFailure,
                "The definition store could not complete the layout save.");
        }
        catch (FormatException)
        {
            return Failure(
                DefinitionStoreErrorCode.StorageFailure,
                "Stored definition metadata is corrupt.");
        }
    }

    public async ValueTask<DefinitionStoreError?> SaveGraphAsync(
        IReadOnlyList<DefinitionGraphWrite> writes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
        {
            return null;
        }

        var pending = new List<(PendingWrite Write, long? ExpectedRevision)>(writes.Count);
        foreach (var write in writes)
        {
            if (Prepare(write.Definition) is not { } prepared)
            {
                return new DefinitionStoreError(
                    DefinitionStoreErrorCode.InvalidDefinition,
                    $"Definition '{write.Definition.Key}' cannot be serialized by this application version.");
            }

            pending.Add((prepared, write.ExpectedRevision));
        }

        try
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                var prospective = new Dictionary<DefinitionKey, object>();
                foreach (var (write, _) in pending)
                {
                    prospective[write.Definition.Key] = write.Definition;
                }

                var validator = new SqliteDefinitionGraphValidator(
                    connection,
                    transaction,
                    prospective);
                var problems = await validator.ValidateBatchAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (problems.Count > 0)
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    var problem = problems[0];
                    return new DefinitionStoreError(
                        problem.Kind switch
                        {
                            DefinitionProblemKind.DependencyConflict =>
                                DefinitionStoreErrorCode.DependencyConflict,
                            DefinitionProblemKind.StorageFailure =>
                                DefinitionStoreErrorCode.StorageFailure,
                            _ => DefinitionStoreErrorCode.InvalidDefinition,
                        },
                        problem.Message);
                }

                foreach (var (write, expectedRevision) in pending)
                {
                    var outcome = await WriteAsync(
                            connection,
                            transaction,
                            write,
                            expectedRevision,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (outcome.Revision is null)
                    {
                        await transaction.RollbackAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                        return new DefinitionStoreError(outcome.ErrorCode, outcome.ErrorMessage);
                    }
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DefinitionStoreError(
                DefinitionStoreErrorCode.Cancelled,
                "The graph save was cancelled and rolled back.");
        }
        catch (SqliteException)
        {
            return new DefinitionStoreError(
                DefinitionStoreErrorCode.StorageFailure,
                "The definition store could not complete the graph save.");
        }
        catch (FormatException)
        {
            return new DefinitionStoreError(
                DefinitionStoreErrorCode.StorageFailure,
                "Stored definition metadata is corrupt.");
        }
    }

    private sealed record PendingWrite(
        IDurableDefinition Definition,
        string PayloadJson);

    private readonly record struct WriteOutcome(
        long? Revision,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DefinitionStoreErrorCode ErrorCode,
        string ErrorMessage);

    /// <summary>
    /// The same pre-write pipeline the repository applies: registry validation,
    /// serialization, payload safety, and a parse round-trip so what is stored
    /// is exactly what this build can read back.
    /// </summary>
    private static PendingWrite? Prepare(IDurableDefinition definition)
    {
        if (KnownDefinitionRegistry.ValidateForSave(definition) is not null)
        {
            return null;
        }

        string payloadJson;
        try
        {
            payloadJson = DefinitionJson.Serialize(definition);
        }
        catch (NotSupportedException)
        {
            return null;
        }

        if (!PortablePayloadSafety.TryValidate(payloadJson, out _))
        {
            return null;
        }

        var document = new PortableDefinitionDocument(
            definition.Key.Kind,
            definition.Key.Value,
            definition.SchemaVersion,
            definition.Name,
            payloadJson);
        return KnownDefinitionRegistry.TryParse(document, out var parsed, out _)
            ? new PendingWrite((IDurableDefinition)parsed!, payloadJson)
            : null;
    }

    private async Task<WriteOutcome> WriteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PendingWrite write,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        var definition = write.Definition;
        var now = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (expectedRevision is null)
        {
            command.CommandText = """
                INSERT INTO definitions(
                    kind, id, schema_version, revision, name, payload_json, created_utc, updated_utc)
                VALUES ($kind, $id, $schemaVersion, 1, $name, $payloadJson, $now, $now)
                ON CONFLICT(kind, id) DO NOTHING
                RETURNING revision, created_utc, updated_utc;
                """;
        }
        else
        {
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
            command.Parameters.AddWithValue("$expectedRevision", expectedRevision.Value);
        }

        command.Parameters.AddWithValue("$kind", definition.Key.Kind.Value);
        command.Parameters.AddWithValue("$id", definition.Key.Value);
        command.Parameters.AddWithValue("$schemaVersion", definition.SchemaVersion);
        command.Parameters.AddWithValue("$name", definition.Name);
        command.Parameters.AddWithValue("$payloadJson", write.PayloadJson);
        command.Parameters.AddWithValue("$now", now);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var outcome = new WriteOutcome(
                    reader.GetInt64(0),
                    DateTimeOffset.Parse(
                        reader.GetString(1),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                    DateTimeOffset.Parse(
                        reader.GetString(2),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                    default,
                    string.Empty);
                await ReplaceReferencesAsync(
                        connection,
                        transaction,
                        definition,
                        cancellationToken)
                    .ConfigureAwait(false);
                return outcome;
            }
        }

        return new WriteOutcome(
            null,
            default,
            default,
            DefinitionStoreErrorCode.RevisionConflict,
            expectedRevision is null
                ? $"A definition with identity '{definition.Key}' already exists."
                : $"Definition '{definition.Key}' changed before it could be saved.");
    }

    private static async Task ReplaceReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IDurableDefinition definition,
        CancellationToken cancellationToken)
    {
        var owner = definition.Key;
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

        foreach (var reference in DefinitionReferenceExtractor.Extract(definition).Distinct())
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

    private static DefinitionStoreResult<StoredDefinition<LayoutDefinition>> Failure(
        DefinitionStoreErrorCode code,
        string message) =>
        DefinitionStoreResult<StoredDefinition<LayoutDefinition>>.Failure(
            new DefinitionStoreError(code, message));
}
