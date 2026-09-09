using Asura.Application;
using Asura.Core;
using Microsoft.Data.Sqlite;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteRecentSessionStoreTests
{
    private static readonly DateTimeOffset ReferenceTime = new(
        2026,
        7,
        22,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task RoundTripsMetadataInLastUsedOrderAndFiltersByDefinitionKind()
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var store = CreateStore(temporary, time);
        var older = Started(
            "session-older",
            DefinitionKind.Connection,
            PanelKind.Terminal,
            "Production shell",
            ReferenceTime.AddMinutes(-20));
        var newer = Started(
            "session-newer",
            DefinitionKind.Screen,
            PanelKind.FileViewer,
            "Operations screen",
            ReferenceTime.AddMinutes(-10));

        AssertSuccess(await store.RecordStartedAsync(older, CancellationToken.None));
        AssertSuccess(await store.RecordStartedAsync(newer, CancellationToken.None));
        AssertSuccess(await store.RecordCompletedAsync(
            new RecentSessionCompletion(
                older.SessionId,
                ReferenceTime.AddMinutes(-5),
                RecentSessionOutcome.GracefullyClosed),
            CancellationToken.None));

        var all = Success(await store.ListRecentAsync(
            new RecentSessionQuery(),
            CancellationToken.None));
        var screens = Success(await store.ListRecentAsync(
            new RecentSessionQuery(sourceKind: DefinitionKind.Screen),
            CancellationToken.None));

        Assert.Equal([older.SessionId, newer.SessionId], all.Select(item => item.SessionId));
        Assert.Equal(RecentSessionOutcome.GracefullyClosed, all[0].Outcome);
        Assert.Equal(ReferenceTime.AddMinutes(-5), all[0].EndedAt);
        Assert.Equal(newer, Assert.Single(screens));
    }

    [Fact]
    public async Task CountAndQueryLimitsCannotLeakAnUnboundedHistory()
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var store = new SqliteRecentSessionStore(
            temporary.Database,
            time,
            new RecentSessionRetentionPolicy(2, TimeSpan.FromDays(30)));
        for (var i = 0; i < 4; i++)
        {
            AssertSuccess(await store.RecordStartedAsync(
                Started(
                    $"session-{i}",
                    DefinitionKind.Connection,
                    PanelKind.Terminal,
                    $"Connection {i}",
                    ReferenceTime.AddMinutes(i)),
                CancellationToken.None));
        }

        var sessions = Success(await store.ListRecentAsync(
            new RecentSessionQuery(limit: 1),
            CancellationToken.None));

        Assert.Equal("session-3", Assert.Single(sessions).SessionId.Value);
        Assert.Equal(2, await CountRowsAsync(temporary));
    }

    [Fact]
    public async Task AgeRetentionIsEnforcedWhenHistoryIsRead()
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var store = new SqliteRecentSessionStore(
            temporary.Database,
            time,
            new RecentSessionRetentionPolicy(100, TimeSpan.FromDays(7)));
        AssertSuccess(await store.RecordStartedAsync(
            Started(
                "session-expiring",
                DefinitionKind.Connection,
                PanelKind.Terminal,
                "Temporary shell",
                ReferenceTime),
            CancellationToken.None));
        time.UtcNow = ReferenceTime.AddDays(8);

        var sessions = Success(await store.ListRecentAsync(
            new RecentSessionQuery(),
            CancellationToken.None));

        Assert.Empty(sessions);
        Assert.Equal(0, await CountRowsAsync(temporary));
    }

    [Fact]
    public async Task DisabledRetentionDeletesExistingHistoryAndPersistsNothingNew()
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var enabled = CreateStore(temporary, time);
        AssertSuccess(await enabled.RecordStartedAsync(
            Started(
                "session-existing",
                DefinitionKind.Connection,
                PanelKind.Terminal,
                "Existing shell",
                ReferenceTime),
            CancellationToken.None));
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE recent_sessions
                SET started_utc = 'corrupt-metadata'
                WHERE session_id = 'session-existing';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var disabled = new SqliteRecentSessionStore(
            temporary.Database,
            time,
            new RecentSessionRetentionPolicy(0, TimeSpan.FromDays(1)));

        AssertSuccess(await disabled.RecordStartedAsync(
            Started(
                "session-forgotten",
                DefinitionKind.Connection,
                PanelKind.Terminal,
                "Forgotten shell",
                ReferenceTime),
            CancellationToken.None));
        var sessions = Success(await disabled.ListRecentAsync(
            new RecentSessionQuery(),
            CancellationToken.None));

        Assert.Empty(sessions);
        Assert.Equal(0, await CountRowsAsync(temporary));
    }

    /// <summary>
    /// A fresh database retains nothing. Keeping a record of every session
    /// someone opens is theirs to ask for, so the store starts with it off and
    /// the opt-in migration is what set the revision.
    /// </summary>
    [Fact]
    public async Task DynamicStoreStartsWithHistoryTurnedOff()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteRecentSessionStore(
            temporary.Database,
            new MutableTimeProvider(ReferenceTime));

        var stored = Success(await store.GetRetentionAsync(CancellationToken.None));

        Assert.False(stored.Policy.IsEnabled);
        Assert.Equal(0, stored.Policy.MaximumEntries);
    }

    /// <summary>
    /// History is off until it is asked for, so every test that expects a
    /// session to be kept has to ask first. It returns the revision that opting
    /// in produced, because the next update has to be made against it.
    /// </summary>
    private static async Task<long> EnableRetentionAsync(SqliteRecentSessionStore store)
    {
        var current = Success(await store.GetRetentionAsync(CancellationToken.None));
        var update = Success(await store.UpdateRetentionAsync(
            RecentSessionRetentionPolicy.Default,
            current.Revision,
            CancellationToken.None));
        return update.StoredPolicy.Revision;
    }

    [Fact]
    public async Task RetentionUpdatePrunesAtomicallyAndDynamicStoreUsesNewRevision()
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var store = new SqliteRecentSessionStore(temporary.Database, time);
        var enabledRevision = await EnableRetentionAsync(store);
        for (var i = 0; i < 3; i++)
        {
            AssertSuccess(await store.RecordStartedAsync(
                Started(
                    $"session-dynamic-{i}",
                    DefinitionKind.Connection,
                    PanelKind.Terminal,
                    $"Dynamic connection {i}",
                    ReferenceTime.AddMinutes(i)),
                CancellationToken.None));
        }

        var update = Success(await store.UpdateRetentionAsync(
            new RecentSessionRetentionPolicy(1, TimeSpan.FromDays(30)),
            enabledRevision,
            CancellationToken.None));
        var staleUpdate = await store.UpdateRetentionAsync(
            new RecentSessionRetentionPolicy(2, TimeSpan.FromDays(30)),
            enabledRevision,
            CancellationToken.None);
        AssertSuccess(await store.RecordStartedAsync(
            Started(
                "session-dynamic-new",
                DefinitionKind.Screen,
                PanelKind.Terminal,
                "Dynamic screen",
                ReferenceTime.AddMinutes(4)),
            CancellationToken.None));
        var sessions = Success(await store.ListRecentAsync(
            new RecentSessionQuery(),
            CancellationToken.None));
        var stored = Success(await store.GetRetentionAsync(CancellationToken.None));

        Assert.Equal(2, update.PrunedSessionCount);
        Assert.Equal(enabledRevision + 1, update.StoredPolicy.Revision);
        Assert.Equal(1, update.StoredPolicy.Policy.MaximumEntries);
        Assert.False(staleUpdate.IsSuccess);
        Assert.Equal(RecentSessionStoreErrorCode.Conflict, staleUpdate.Error!.Code);
        Assert.Equal(update.StoredPolicy, stored);
        Assert.Equal(
            new SessionId("session-dynamic-new"),
            Assert.Single(sessions).SessionId);
        Assert.Equal(1, await CountRowsAsync(temporary));
    }

    [Fact]
    public async Task ZeroRetentionClearsHistoryAndDisablesFutureRecording()
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var store = new SqliteRecentSessionStore(temporary.Database, time);
        var enabledRevision = await EnableRetentionAsync(store);
        AssertSuccess(await store.RecordStartedAsync(
            Started(
                "session-before-disable",
                DefinitionKind.Connection,
                PanelKind.Terminal,
                "Recorded before disable",
                ReferenceTime),
            CancellationToken.None));

        var update = Success(await store.UpdateRetentionAsync(
            new RecentSessionRetentionPolicy(0, TimeSpan.FromDays(30)),
            enabledRevision,
            CancellationToken.None));
        AssertSuccess(await store.RecordStartedAsync(
            Started(
                "session-after-disable",
                DefinitionKind.Connection,
                PanelKind.Terminal,
                "Recorded after disable",
                ReferenceTime.AddMinutes(1)),
            CancellationToken.None));
        var sessions = Success(await store.ListRecentAsync(
            new RecentSessionQuery(),
            CancellationToken.None));

        Assert.Equal(1, update.PrunedSessionCount);
        Assert.False(update.StoredPolicy.Policy.IsEnabled);
        Assert.Empty(sessions);
        Assert.Equal(0, await CountRowsAsync(temporary));
    }

    [Theory]
    [InlineData("DELETE FROM recent_session_retention;")]
    [InlineData(
        "PRAGMA ignore_check_constraints = ON; "
        + "UPDATE recent_session_retention SET maximum_age_ticks = 0;")]
    public async Task MissingOrCorruptRetentionFailsClosed(string corruptSql)
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var store = new SqliteRecentSessionStore(temporary.Database, time);
        _ = await EnableRetentionAsync(store);
        AssertSuccess(await store.RecordStartedAsync(
            Started(
                "session-preserved",
                DefinitionKind.Connection,
                PanelKind.Terminal,
                "Preserved session",
                ReferenceTime),
            CancellationToken.None));
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = corruptSql;
            await command.ExecuteNonQueryAsync();
        }

        var get = await store.GetRetentionAsync(CancellationToken.None);
        var record = await store.RecordStartedAsync(
            Started(
                "session-rejected",
                DefinitionKind.Connection,
                PanelKind.Terminal,
                "Rejected session",
                ReferenceTime.AddMinutes(1)),
            CancellationToken.None);
        var list = await store.ListRecentAsync(
            new RecentSessionQuery(),
            CancellationToken.None);

        Assert.Equal(RecentSessionStoreErrorCode.InvalidRetentionData, get.Error!.Code);
        Assert.Equal(RecentSessionStoreErrorCode.InvalidRetentionData, record.Error!.Code);
        Assert.Equal(RecentSessionStoreErrorCode.InvalidRetentionData, list.Error!.Code);
        Assert.Equal(1, await CountRowsAsync(temporary));
    }

    [Fact]
    public async Task FailedPruneRollsBackRetentionUpdate()
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var store = new SqliteRecentSessionStore(temporary.Database, time);
        var enabledRevision = await EnableRetentionAsync(store);
        AssertSuccess(await store.RecordStartedAsync(
            Started(
                "session-protected",
                DefinitionKind.Connection,
                PanelKind.Terminal,
                "Protected session",
                ReferenceTime),
            CancellationToken.None));
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER prevent_recent_session_delete
                BEFORE DELETE ON recent_sessions
                BEGIN
                    SELECT RAISE(ABORT, 'delete blocked for atomicity test');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var update = await store.UpdateRetentionAsync(
            new RecentSessionRetentionPolicy(0, TimeSpan.FromDays(30)),
            enabledRevision,
            CancellationToken.None);
        var stored = Success(await store.GetRetentionAsync(CancellationToken.None));

        Assert.False(update.IsSuccess);
        Assert.Equal(RecentSessionStoreErrorCode.StorageFailure, update.Error!.Code);
        Assert.Equal(
            new StoredRecentSessionRetentionPolicy(
                RecentSessionRetentionPolicy.Default,
                enabledRevision),
            stored);
        Assert.Equal(1, await CountRowsAsync(temporary));
    }

    [Fact]
    public async Task StartAndCompletionAreIdempotentButConflictingMetadataIsRejected()
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var store = CreateStore(temporary, time);
        var started = Started(
            "session-1",
            DefinitionKind.Connection,
            PanelKind.Terminal,
            "Production shell",
            ReferenceTime.AddMinutes(-1));
        AssertSuccess(await store.RecordStartedAsync(started, CancellationToken.None));
        AssertSuccess(await store.RecordStartedAsync(started, CancellationToken.None));

        var conflict = await store.RecordStartedAsync(
            Started(
                "session-1",
                DefinitionKind.Connection,
                PanelKind.Terminal,
                "Different definition title",
                started.StartedAt),
            CancellationToken.None);
        var completion = new RecentSessionCompletion(
            started.SessionId,
            ReferenceTime,
            RecentSessionOutcome.Failed);
        AssertSuccess(await store.RecordCompletedAsync(completion, CancellationToken.None));
        AssertSuccess(await store.RecordCompletedAsync(completion, CancellationToken.None));
        var completionConflict = await store.RecordCompletedAsync(
            new RecentSessionCompletion(
                started.SessionId,
                ReferenceTime,
                RecentSessionOutcome.GracefullyClosed),
            CancellationToken.None);

        Assert.Equal(RecentSessionStoreErrorCode.Conflict, conflict.Error!.Code);
        Assert.Equal(RecentSessionStoreErrorCode.Conflict, completionConflict.Error!.Code);
    }

    [Fact]
    public async Task ClearThroughIsScopedAndLateCompletionDoesNotRecreateHistory()
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var store = CreateStore(temporary, time);
        var cleared = Started(
            "session-cleared",
            DefinitionKind.Connection,
            PanelKind.Terminal,
            "Old shell",
            ReferenceTime.AddHours(-1));
        var retained = Started(
            "session-retained",
            DefinitionKind.Screen,
            PanelKind.Terminal,
            "New screen",
            ReferenceTime.AddMinutes(1));
        AssertSuccess(await store.RecordStartedAsync(cleared, CancellationToken.None));
        AssertSuccess(await store.RecordStartedAsync(retained, CancellationToken.None));

        var deleted = Success(await store.ClearThroughAsync(
            ReferenceTime,
            CancellationToken.None));
        AssertSuccess(await store.RecordCompletedAsync(
            new RecentSessionCompletion(
                cleared.SessionId,
                ReferenceTime.AddMinutes(2),
                RecentSessionOutcome.GracefullyClosed),
            CancellationToken.None));
        var sessions = Success(await store.ListRecentAsync(
            new RecentSessionQuery(),
            CancellationToken.None));

        Assert.Equal(1, deleted);
        Assert.Equal(retained, Assert.Single(sessions));
    }

    [Fact]
    public async Task StartupReconciliationMarksOnlyActiveSessionsInterrupted()
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var store = CreateStore(temporary, time);
        var active = Started(
            "session-active",
            DefinitionKind.Connection,
            PanelKind.Terminal,
            "Active shell",
            ReferenceTime.AddMinutes(-10));
        var completed = Started(
            "session-completed",
            DefinitionKind.Screen,
            PanelKind.FileViewer,
            "Completed screen",
            ReferenceTime.AddMinutes(-20));
        AssertSuccess(await store.RecordStartedAsync(active, CancellationToken.None));
        AssertSuccess(await store.RecordStartedAsync(completed, CancellationToken.None));
        AssertSuccess(await store.RecordCompletedAsync(
            new RecentSessionCompletion(
                completed.SessionId,
                ReferenceTime.AddMinutes(-15),
                RecentSessionOutcome.GracefullyClosed),
            CancellationToken.None));

        var affected = Success(await store.MarkActiveSessionsInterruptedAsync(
            CancellationToken.None));
        var sessions = Success(await store.ListRecentAsync(
            new RecentSessionQuery(),
            CancellationToken.None));

        Assert.Equal(1, affected);
        var interrupted = Assert.Single(sessions, item => item.SessionId == active.SessionId);
        Assert.Equal(RecentSessionOutcome.Interrupted, interrupted.Outcome);
        Assert.Equal(ReferenceTime, interrupted.EndedAt);
        Assert.Equal(
            RecentSessionOutcome.GracefullyClosed,
            Assert.Single(sessions, item => item.SessionId == completed.SessionId).Outcome);
    }

    [Fact]
    public async Task SchemaContainsOnlyTheClosedMetadataColumns()
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(recent_sessions);";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Equal(
            [
                "session_id",
                "definition_kind",
                "definition_id",
                "panel_kind",
                "title",
                "started_utc",
                "ended_utc",
                "outcome",
            ],
            columns, StringComparer.Ordinal);
        Assert.DoesNotContain(columns, column =>
            column.Contains("payload", StringComparison.OrdinalIgnoreCase)
            || column.Contains("content", StringComparison.OrdinalIgnoreCase)
            || column.Contains("command", StringComparison.OrdinalIgnoreCase)
            || column.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || column.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CorruptMetadataReturnsAResettableFailureInsteadOfPartialHistory()
    {
        await using var temporary = TemporaryDatabase.Create();
        var time = new MutableTimeProvider(ReferenceTime);
        var store = CreateStore(temporary, time);
        AssertSuccess(await store.RecordStartedAsync(
            Started(
                "session-corrupt",
                DefinitionKind.Connection,
                PanelKind.Terminal,
                "Production shell",
                ReferenceTime),
            CancellationToken.None));
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE recent_sessions
                SET started_utc = 'not-a-timestamp'
                WHERE session_id = 'session-corrupt';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var result = await store.ListRecentAsync(
            new RecentSessionQuery(),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(RecentSessionStoreErrorCode.InvalidHistoryData, result.Error!.Code);
        Assert.Equal(1, Success(await store.ClearAllAsync(CancellationToken.None)));
        Assert.Empty(Success(await store.ListRecentAsync(
            new RecentSessionQuery(),
            CancellationToken.None)));
    }

    private static SqliteRecentSessionStore CreateStore(
        TemporaryDatabase temporary,
        TimeProvider timeProvider) =>
        new(
            temporary.Database,
            timeProvider,
            new RecentSessionRetentionPolicy(100, TimeSpan.FromDays(30)));

    private static RecentSessionRecord Started(
        string sessionId,
        DefinitionKind sourceKind,
        PanelKind panelKind,
        string title,
        DateTimeOffset startedAt) =>
        new(
            new SessionId(sessionId),
            new DefinitionKey(sourceKind, $"definition-{sessionId}"),
            panelKind,
            title,
            startedAt,
            endedAt: null,
            RecentSessionOutcome.Active);

    private static void AssertSuccess(RecentSessionStoreResult<Unit> result) =>
        Assert.True(result.IsSuccess, result.Error?.Message);

    private static T Success<T>(RecentSessionStoreResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    private static async Task<long> CountRowsAsync(TemporaryDatabase temporary)
    {
        await using var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM recent_sessions;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public MutableTimeProvider(DateTimeOffset utcNow) => UtcNow = utcNow;

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
