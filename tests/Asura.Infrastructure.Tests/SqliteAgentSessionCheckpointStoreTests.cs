using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteAgentSessionCheckpointStoreTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SaveLoadListDeleteSurviveDatabaseReopen()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database);
        var older = Checkpoint("run-older", 2, 3, "{\"message\":\"older\"}", Baseline);
        var newer = Checkpoint(
            "run-newer",
            4,
            6,
            "{\"message\":\"newer\"}",
            Baseline.AddMinutes(1));

        Assert.True((await store.SaveAsync(older, CancellationToken.None)).IsSuccess);
        Assert.True((await store.SaveAsync(newer, CancellationToken.None)).IsSuccess);
        await temporary.ReopenAsync();
        store = new SqliteAgentSessionCheckpointStore(temporary.Database);

        var loaded = Success(await store.LoadAsync(
            newer.RunId,
            CancellationToken.None));
        var listed = Success(await store.ListAsync(10, CancellationToken.None));
        var deleted = Success(await store.DeleteAsync(
            newer.RunId,
            CancellationToken.None));
        var deletedAgain = Success(await store.DeleteAsync(
            newer.RunId,
            CancellationToken.None));

        Assert.Equal(newer.RunId, loaded.RunId);
        Assert.Equal(newer.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(newer.Generation, loaded.Generation);
        Assert.Equal(newer.Revision, loaded.Revision);
        Assert.Equal(newer.PayloadJson, loaded.PayloadJson);
        Assert.Equal(newer.UpdatedAt, loaded.UpdatedAt);
        Assert.Equal([newer.RunId, older.RunId], listed.Select(item => item.RunId));
        Assert.True(deleted);
        Assert.False(deletedAgain);
        var missing = await store.LoadAsync(newer.RunId, CancellationToken.None);
        Assert.False(missing.IsSuccess);
        Assert.Equal(AgentSessionCheckpointStoreErrorCode.NotFound, missing.Error?.Code);
    }

    [Fact]
    public async Task ScopedOperationsNeverExposeAnotherWorkspacesConversations()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database);
        var main = new AgentConversationScopeId("workspace-main");
        var quickTerminal = new AgentConversationScopeId("workspace-quick-terminal");
        var mainCheckpoint = Checkpoint(
            "run-main",
            1,
            1,
            "{\"message\":\"main\"}",
            Baseline);
        var quickCheckpoint = Checkpoint(
            "run-quick-terminal",
            1,
            1,
            "{\"message\":\"quick\"}",
            Baseline.AddSeconds(1));

        Assert.True((await store.SaveAsync(
            main,
            mainCheckpoint,
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.SaveAsync(
            quickTerminal,
            quickCheckpoint,
            CancellationToken.None)).IsSuccess);

        var mainList = Success(await store.ListAsync(main, 10, CancellationToken.None));
        var quickList = Success(await store.ListAsync(
            quickTerminal,
            10,
            CancellationToken.None));
        var crossWorkspaceLoad = await store.LoadAsync(
            quickTerminal,
            mainCheckpoint.RunId,
            CancellationToken.None);
        var crossWorkspaceDelete = Success(await store.DeleteAsync(
            quickTerminal,
            mainCheckpoint.RunId,
            CancellationToken.None));

        Assert.Equal([mainCheckpoint.RunId], mainList.Select(item => item.RunId));
        Assert.Equal([quickCheckpoint.RunId], quickList.Select(item => item.RunId));
        Assert.False(crossWorkspaceLoad.IsSuccess);
        Assert.Equal(
            AgentSessionCheckpointStoreErrorCode.NotFound,
            crossWorkspaceLoad.Error?.Code);
        Assert.False(crossWorkspaceDelete);
        Assert.True((await store.LoadAsync(
            main,
            mainCheckpoint.RunId,
            CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task StaleAndSameRevisionDifferentWritesCannotReplaceCheckpoint()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database);
        var original = Checkpoint("run-one", 3, 5, "{\"value\":1}", Baseline);
        Assert.True((await store.SaveAsync(original, CancellationToken.None)).IsSuccess);

        var sameRevision = await store.SaveAsync(
            Checkpoint("run-one", 3, 5, "{\"value\":2}", Baseline.AddSeconds(1)),
            CancellationToken.None);
        var stale = await store.SaveAsync(
            Checkpoint("run-one", 1, 4, "{\"value\":0}", Baseline.AddSeconds(2)),
            CancellationToken.None);
        var idempotent = await store.SaveAsync(original, CancellationToken.None);

        Assert.False(sameRevision.IsSuccess);
        Assert.Equal(
            AgentSessionCheckpointStoreErrorCode.RevisionConflict,
            sameRevision.Error?.Code);
        Assert.Equal(5, sameRevision.Error?.CurrentRevision);
        Assert.False(stale.IsSuccess);
        Assert.Equal(
            AgentSessionCheckpointStoreErrorCode.RevisionConflict,
            stale.Error?.Code);
        Assert.True(idempotent.IsSuccess);
        Assert.Equal(
            original.PayloadJson,
            Success(await store.LoadAsync(original.RunId, CancellationToken.None))
                .PayloadJson);
    }

    [Fact]
    public async Task SameRevisionTimestampChangeIsNotIdempotent()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database);
        var original = Checkpoint("run-timestamp-conflict", 3, 5, "{\"value\":1}", Baseline);
        Assert.True((await store.SaveAsync(original, CancellationToken.None)).IsSuccess);

        var changedTimestamp = await store.SaveAsync(
            Checkpoint(
                original.RunId.Value,
                original.Generation,
                original.Revision,
                original.PayloadJson,
                Baseline.AddSeconds(1)),
            CancellationToken.None);

        Assert.False(changedTimestamp.IsSuccess);
        Assert.Equal(
            AgentSessionCheckpointStoreErrorCode.RevisionConflict,
            changedTimestamp.Error?.Code);
        Assert.Equal(original.Revision, changedTimestamp.Error?.CurrentRevision);
        Assert.Equal(
            original.UpdatedAt,
            Success(await store.LoadAsync(original.RunId, CancellationToken.None)).UpdatedAt);
    }

    [Fact]
    public async Task ConcurrentNextRevisionWritesHaveOneAtomicWinner()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database);
        var initial = Checkpoint("run-race", 1, 1, "{\"value\":0}", Baseline);
        Assert.True((await store.SaveAsync(initial, CancellationToken.None)).IsSuccess);
        var first = Checkpoint(
            "run-race",
            2,
            2,
            "{\"winner\":\"first\"}",
            Baseline.AddSeconds(1));
        var second = Checkpoint(
            "run-race",
            2,
            2,
            "{\"winner\":\"second\"}",
            Baseline.AddSeconds(1));

        var writes = await Task.WhenAll(
            store.SaveAsync(first, CancellationToken.None).AsTask(),
            store.SaveAsync(second, CancellationToken.None).AsTask());

        Assert.Single(writes, result => result.IsSuccess);
        Assert.Single(writes, result =>
            result.Error?.Code
                == AgentSessionCheckpointStoreErrorCode.RevisionConflict);
        var loaded = Success(await store.LoadAsync(
            initial.RunId,
            CancellationToken.None));
        Assert.Contains(
            loaded.PayloadJson,
            [first.PayloadJson, second.PayloadJson], StringComparer.Ordinal);
    }

    [Fact]
    public async Task TamperedPayloadFailsIntegrityValidationWithoutDeserialization()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database);
        var checkpoint = Checkpoint(
            "run-corrupt",
            1,
            1,
            "{\"message\":\"safe\"}",
            Baseline);
        Assert.True((await store.SaveAsync(checkpoint, CancellationToken.None)).IsSuccess);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_session_checkpoints
                SET payload_json = '{"message":"tampered"}'
                WHERE run_id = 'run-corrupt';
                """;
            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
        }

        var loaded = await store.LoadAsync(
            checkpoint.RunId,
            CancellationToken.None);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(
            AgentSessionCheckpointStoreErrorCode.CorruptData,
            loaded.Error?.Code);
    }

    [Fact]
    public async Task TamperedTimestampFailsIntegrityValidation()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database);
        var checkpoint = Checkpoint(
            "run-timestamp-tamper",
            1,
            1,
            "{\"message\":\"safe\"}",
            Baseline);
        Assert.True((await store.SaveAsync(checkpoint, CancellationToken.None)).IsSuccess);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_session_checkpoints
                SET updated_utc = $updatedUtc
                WHERE run_id = $runId;
                """;
            command.Parameters.AddWithValue(
                "$updatedUtc",
                Baseline.AddMinutes(1).ToString("O"));
            command.Parameters.AddWithValue("$runId", checkpoint.RunId.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
        }

        var loaded = await store.LoadAsync(
            checkpoint.RunId,
            CancellationToken.None);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(
            AgentSessionCheckpointStoreErrorCode.CorruptData,
            loaded.Error?.Code);
    }

    [Fact]
    public async Task TamperedWorkspaceScopeFailsIntegrityValidation()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database);
        var originalWorkspace = new AgentConversationScopeId("workspace-original");
        var attackerWorkspace = new AgentConversationScopeId("workspace-attacker");
        var checkpoint = Checkpoint(
            "run-workspace-tamper",
            1,
            1,
            "{\"message\":\"safe\"}",
            Baseline);
        Assert.True((await store.SaveAsync(
            originalWorkspace,
            checkpoint,
            CancellationToken.None)).IsSuccess);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE agent_session_checkpoints
                SET workspace_id = $workspaceId
                WHERE run_id = $runId;
                """;
            command.Parameters.AddWithValue("$workspaceId", attackerWorkspace.Value);
            command.Parameters.AddWithValue("$runId", checkpoint.RunId.Value);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
        }

        var loaded = await store.LoadAsync(
            attackerWorkspace,
            checkpoint.RunId,
            CancellationToken.None);

        Assert.False(loaded.IsSuccess);
        Assert.Equal(
            AgentSessionCheckpointStoreErrorCode.CorruptData,
            loaded.Error?.Code);
    }

    [Fact]
    public async Task CancelledOperationsAndUnboundedListsFailDeterministically()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var save = await store.SaveAsync(
            Checkpoint("run-cancelled", 0, 0, "{}", Baseline),
            cancellation.Token);

        Assert.False(save.IsSuccess);
        Assert.Equal(AgentSessionCheckpointStoreErrorCode.Cancelled, save.Error?.Code);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ListAsync(
                    SqliteAgentSessionCheckpointStore.MaximumListedCheckpoints + 1,
                    CancellationToken.None)
                .AsTask());
    }

    [Fact]
    public void CheckpointEnvelopeRejectsCredentialFieldsAndLiteralTokens()
    {
        var timestamp = Baseline;

        Assert.Throws<ArgumentException>(() => new AgentSessionCheckpoint(
            new AgentRunId("run-secret-field"),
            AgentSessionCheckpoint.CurrentSchemaVersion,
            0,
            0,
            "{\"secretRef\":\"vault-entry\"}",
            timestamp));
        Assert.Throws<ArgumentException>(() => new AgentSessionCheckpoint(
            new AgentRunId("run-literal-token"),
            AgentSessionCheckpoint.CurrentSchemaVersion,
            0,
            0,
            "{\"message\":\"api_key=literal-secret-value\"}",
            timestamp));
    }

    private static AgentSessionCheckpoint Checkpoint(
        string runId,
        long generation,
        long revision,
        string payload,
        DateTimeOffset updatedAt) =>
        new(
            new AgentRunId(runId),
            AgentSessionCheckpoint.CurrentSchemaVersion,
            generation,
            revision,
            payload,
            updatedAt);

    private static T Success<T>(AgentSessionCheckpointStoreResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        return result.Value;
    }
}
