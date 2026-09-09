using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteAgentRunHistoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MetadataRoundTripsAndRetentionUsesRevisionFence()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(
            temporary.Database,
            new FixedTimeProvider(Now));
        var scope = new AgentConversationScopeId("workspace-history");
        var metadata = Metadata("run-history", Now);

        Assert.True((await store.SaveHistoryMetadataAsync(
            scope,
            metadata,
            CancellationToken.None)).IsSuccess);
        var loaded = Success(await store.LoadHistoryMetadataAsync(
            scope,
            metadata.RunId,
            CancellationToken.None));
        var originalRetention = Success(await store.GetHistoryRetentionAsync(
            CancellationToken.None));
        var updated = Success(await store.UpdateHistoryRetentionAsync(
            scope,
            originalRetention,
            10,
            TimeSpan.FromDays(30),
            protectedRunId: null,
            CancellationToken.None));
        var stale = await store.UpdateHistoryRetentionAsync(
            scope,
            originalRetention,
            50,
            TimeSpan.FromDays(90),
            protectedRunId: null,
            CancellationToken.None);

        Assert.Equal(metadata.RunId, loaded.RunId);
        Assert.Equal(metadata.ProviderId, loaded.ProviderId);
        Assert.Equal(metadata.ModelId, loaded.ModelId);
        Assert.Equal(metadata.PolicyGeneration, loaded.PolicyGeneration);
        Assert.Equal(metadata.UpdatedAtUtc, loaded.UpdatedAtUtc);
        Assert.True(metadata.BaselinePolicy.Permissions.SequenceEqual(
            loaded.BaselinePolicy.Permissions));
        Assert.True(metadata.RunPolicy.Permissions.SequenceEqual(
            loaded.RunPolicy.Permissions));
        Assert.True(metadata.EffectivePolicy.Permissions.SequenceEqual(
            loaded.EffectivePolicy.Permissions));
        Assert.Equal(originalRetention.Revision + 1, updated.Revision);
        Assert.Equal(10, updated.MaximumRuns);
        Assert.False(stale.IsSuccess);
        Assert.Equal(
            AgentSessionCheckpointStoreErrorCode.RevisionConflict,
            stale.Error?.Code);
        Assert.Equal(updated.Revision, stale.Error?.CurrentRevision);
    }

    [Fact]
    public async Task RetentionPrunesImmediatelyButProtectsTheActiveRun()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(
            temporary.Database,
            new FixedTimeProvider(Now));
        var scope = new AgentConversationScopeId("workspace-prune");
        foreach (var (runId, updatedAt) in new[]
                 {
                     ("run-old", Now.AddDays(-20)),
                     ("run-middle", Now.AddDays(-10)),
                     ("run-active", Now.AddDays(-30)),
                 })
        {
            Assert.True((await store.SaveAsync(
                scope,
                Checkpoint(runId, updatedAt),
                CancellationToken.None)).IsSuccess);
            Assert.True((await store.SaveHistoryMetadataAsync(
                scope,
                Metadata(runId, updatedAt),
                CancellationToken.None)).IsSuccess);
        }

        var retention = Success(await store.GetHistoryRetentionAsync(
            CancellationToken.None));
        Assert.True((await store.UpdateHistoryRetentionAsync(
            scope,
            retention,
            maximumRuns: 1,
            maximumAge: TimeSpan.FromDays(5),
            protectedRunId: new AgentRunId("run-active"),
            CancellationToken.None)).IsSuccess);

        var listed = Success(await store.ListAsync(scope, 10, CancellationToken.None));
        Assert.Equal([new AgentRunId("run-active")], listed.Select(item => item.RunId));
        Assert.True((await store.LoadHistoryMetadataAsync(
            scope,
            new AgentRunId("run-active"),
            CancellationToken.None)).IsSuccess);
        Assert.False((await store.LoadHistoryMetadataAsync(
            scope,
            new AgentRunId("run-old"),
            CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task RetentionIncludesCheckpointOnlyRunsAfterMetadataFailureAndRestart()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database, new FixedTimeProvider(Now));
        var scope = new AgentConversationScopeId("checkpoint-only");
        foreach (var (id, timestamp) in new[]
                 {
                     ("orphan-old", Now.AddDays(-20)),
                     ("orphan-middle", Now.AddDays(-2)),
                     ("orphan-new", Now.AddDays(-1)),
                 })
        {
            Assert.True((await store.SaveAsync(scope, Checkpoint(id, timestamp), CancellationToken.None)).IsSuccess);
        }

        await using (var connection = await temporary.Database.OpenConnectionAsync(CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_history_metadata BEFORE INSERT ON agent_run_history_metadata
                BEGIN SELECT RAISE(ABORT, 'simulated metadata failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        Assert.False((await store.SaveHistoryMetadataAsync(scope, Metadata("orphan-new", Now.AddDays(-1)), CancellationToken.None)).IsSuccess);
        await temporary.ReopenAsync();
        store = new SqliteAgentSessionCheckpointStore(temporary.Database, new FixedTimeProvider(Now));

        var retention = Success(await store.GetHistoryRetentionAsync(CancellationToken.None));
        Assert.True((await store.UpdateHistoryRetentionAsync(scope, retention, 1, TimeSpan.FromDays(5), null, CancellationToken.None)).IsSuccess);

        Assert.Equal([new AgentRunId("orphan-new")],
            Success(await store.ListAsync(scope, 10, CancellationToken.None)).Select(item => item.RunId));
        Assert.False((await store.LoadAsync(scope, new AgentRunId("orphan-old"), CancellationToken.None)).IsSuccess);
        Assert.False((await store.LoadAsync(scope, new AgentRunId("orphan-middle"), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task CheckpointOnlySaveEnforcesCountWithoutWaitingForMetadata()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database, new FixedTimeProvider(Now));
        var retention = Success(await store.GetHistoryRetentionAsync(CancellationToken.None));
        Assert.True((await store.UpdateHistoryRetentionAsync(null, retention, 1, TimeSpan.FromDays(5), null, CancellationToken.None)).IsSuccess);
        Assert.True((await store.SaveAsync(Checkpoint("first", Now.AddDays(-1)), CancellationToken.None)).IsSuccess);
        Assert.True((await store.SaveAsync(Checkpoint("second", Now), CancellationToken.None)).IsSuccess);

        Assert.Equal([new AgentRunId("second")],
            Success(await store.ListAsync(10, CancellationToken.None)).Select(item => item.RunId));
    }

    [Fact]
    public async Task RetentionRanksLatestActivityAcrossCheckpointAndMetadata()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(temporary.Database, new FixedTimeProvider(Now));
        Assert.True((await store.SaveAsync(Checkpoint("new-checkpoint", Now), CancellationToken.None)).IsSuccess);
        Assert.True((await store.SaveHistoryMetadataAsync(null, Metadata("new-checkpoint", Now.AddDays(-20)), CancellationToken.None)).IsSuccess);
        Assert.True((await store.SaveAsync(Checkpoint("older-checkpoint", Now.AddDays(-1)), CancellationToken.None)).IsSuccess);
        var retention = Success(await store.GetHistoryRetentionAsync(CancellationToken.None));
        Assert.True((await store.UpdateHistoryRetentionAsync(null, retention, 1, TimeSpan.FromDays(5), null, CancellationToken.None)).IsSuccess);

        Assert.Equal([new AgentRunId("new-checkpoint")],
            Success(await store.ListAsync(10, CancellationToken.None)).Select(item => item.RunId));
    }

    [Fact]
    public async Task ExportIsDeterministicAllowlistedAndOmitsCheckpointContent()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(
            temporary.Database,
            new FixedTimeProvider(Now));
        var scope = new AgentConversationScopeId("workspace-export");
        var checkpoint = new AgentSessionCheckpoint(
            new AgentRunId("run-export"),
            AgentSessionCheckpoint.CurrentSchemaVersion,
            1,
            1,
            "{\"prompt\":\"never-export-this\",\"toolContent\":\"also-private\"}",
            Now);
        Assert.True((await store.SaveAsync(
            scope,
            checkpoint,
            CancellationToken.None)).IsSuccess);
        Assert.True((await store.SaveHistoryMetadataAsync(
            scope,
            Metadata("run-export", Now),
            CancellationToken.None)).IsSuccess);

        await using var first = new MemoryStream();
        await using var second = new MemoryStream();
        var firstReceipt = Success(await store.ExportHistoryAsync(
            scope,
            first,
            CancellationToken.None));
        var secondReceipt = Success(await store.ExportHistoryAsync(
            scope,
            second,
            CancellationToken.None));
        var firstJson = Encoding.UTF8.GetString(first.ToArray());

        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal(firstReceipt.Sha256, secondReceipt.Sha256);
        Assert.DoesNotContain("never-export-this", firstJson, StringComparison.Ordinal);
        Assert.DoesNotContain("also-private", firstJson, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(firstJson);
        Assert.Equal(
            ["schemaVersion", "exportedAtUtc", "runs", "deletedRuns"],
            document.RootElement.EnumerateObject().Select(item => item.Name),
            StringComparer.Ordinal);
        var run = Assert.Single(document.RootElement.GetProperty("runs").EnumerateArray());
        Assert.Equal(
            [
                "runId",
                "providerProfileId",
                "modelId",
                "policyGeneration",
                "updatedAtUtc",
                "baselinePolicy",
                "runPolicy",
                "effectivePolicy",
                "audit",
            ],
            run.EnumerateObject().Select(item => item.Name),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task ExportFailsClosedForMalformedAuditTrail()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteAgentSessionCheckpointStore(
            temporary.Database,
            new FixedTimeProvider(Now));
        var scope = new AgentConversationScopeId("workspace-corrupt-export");
        Assert.True((await store.SaveHistoryMetadataAsync(
            scope,
            Metadata("run-corrupt-export", Now),
            CancellationToken.None)).IsSuccess);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
                         CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO audit_events(
                    event_id,
                    correlation_id,
                    actor_kind,
                    actor_id,
                    action,
                    target_kind,
                    target_id,
                    outcome,
                    details_json,
                    occurred_utc)
                VALUES (
                    'event-corrupt-export',
                    'action-corrupt-export',
                    'Agent',
                    'actor',
                    'terminal.read',
                    'agent-target-fingerprint',
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    'Succeeded',
                    '{"kind":"agent-action","runId":"run-corrupt-export"}',
                    '2026-08-29T12:00:00.0000000+00:00');
                """;
            Assert.Equal(1, await command.ExecuteNonQueryAsync(CancellationToken.None));
        }

        await using var destination = new MemoryStream();
        var result = await store.ExportHistoryAsync(
            scope,
            destination,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            AgentSessionCheckpointStoreErrorCode.CorruptData,
            result.Error?.Code);
        Assert.Empty(destination.ToArray());
    }

    private static AgentRunHistoryMetadata Metadata(
        string runId,
        DateTimeOffset updatedAt)
    {
        var policy = AgentRunHistoryPolicy.FromPolicy(AgentPolicy.Default);
        return new AgentRunHistoryMetadata(
            new AgentRunId(runId),
            new AiProviderProfileId("profile-id"),
            "model-id",
            policy,
            policy,
            policy,
            1,
            updatedAt);
    }

    private static AgentSessionCheckpoint Checkpoint(
        string runId,
        DateTimeOffset updatedAt) =>
        new(
            new AgentRunId(runId),
            AgentSessionCheckpoint.CurrentSchemaVersion,
            1,
            1,
            "{\"state\":\"safe\"}",
            updatedAt);

    private static T Success<T>(AgentSessionCheckpointStoreResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return Assert.IsAssignableFrom<T>(result.Value);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
