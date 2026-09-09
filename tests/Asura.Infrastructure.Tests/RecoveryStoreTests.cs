using System.Globalization;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class RecoveryStoreTests
{
    [Fact]
    public async Task UnfinishedRunRequiresRecoveryAndDefinitionsRemainIntact()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        var definition = DurableDefinitionFixtures.Layout();
        Assert.True((await repository.SaveAsync(definition, null, CancellationToken.None)).IsSuccess);
        var runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        var first = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.False(first.RecoveryRequired);

        await temporary.ReopenAsync();
        runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        var second = Success(await runStore.BeginRunAsync(CancellationToken.None));

        Assert.True(second.RecoveryRequired);
        Assert.Equal(first.RunId, second.PreviousState.RunId);
        repository = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        Assert.True((await repository.GetAsync(definition.Key, CancellationToken.None)).IsSuccess);
        Assert.True((await runStore.CompleteRunAsync(
            second.RunId,
            CancellationToken.None)).IsSuccess);
        Assert.True((await runStore.GetStateAsync(CancellationToken.None)).Value!.WasClean);
    }

    /// <summary>
    /// A run that died before it changed anything leaves the session it started
    /// from as the newest thing stored, and startup opens that. Losing a
    /// session because the process that would have re-saved it unchanged died
    /// is the one outcome nobody would have chosen.
    /// </summary>
    [Fact]
    public async Task ARunThatSavedNothingLeavesTheLastSavedSessionToRestore()
    {
        await using var temporary = TemporaryDatabase.Create();
        var runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        var recoveryStore = new SqliteRuntimeRecoveryStore(temporary.Database);
        var cleanRun = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(cleanRun.RunId, "window-one", "clean-run"),
            CancellationToken.None)).IsSuccess);
        Assert.True((await runStore.CompleteRunAsync(
            cleanRun.RunId,
            CancellationToken.None)).IsSuccess);

        var interruptedRun = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.False(interruptedRun.RecoveryRequired);
        await temporary.ReopenAsync();

        runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        var currentRun = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.True(currentRun.RecoveryRequired);
        Assert.Equal(interruptedRun.RunId, currentRun.PreviousState.RunId);

        var restored = Success(await StartupRestore(temporary, currentRun)
            .LoadLatestSessionAsync(CancellationToken.None));

        var snapshot = Assert.Single(restored);
        Assert.Equal(cleanRun.RunId, snapshot.RunId);
    }

    /// <summary>
    /// Startup opens what was last stored, and how the process that stored it
    /// ended is not part of the question.
    /// </summary>
    [Fact]
    public async Task StartupRestoresTheNewestStoredSessionAfterAnInterruptedRun()
    {
        await using var temporary = TemporaryDatabase.Create();
        var runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        var recoveryStore = new SqliteRuntimeRecoveryStore(temporary.Database);
        var cleanRun = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(cleanRun.RunId, "shared-key", "clean-run"),
            CancellationToken.None)).IsSuccess);
        Assert.True((await runStore.CompleteRunAsync(
            cleanRun.RunId,
            CancellationToken.None)).IsSuccess);
        var interruptedRun = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(interruptedRun.RunId, "shared-key", "interrupted-run"),
            CancellationToken.None)).IsSuccess);
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(interruptedRun.RunId, "second-window", "interrupted-run"),
            CancellationToken.None)).IsSuccess);
        await temporary.ReopenAsync();

        runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        var currentRun = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.True(currentRun.RecoveryRequired);
        var restore = StartupRestore(temporary, currentRun);

        var restored = Success(await restore.LoadLatestSessionAsync(CancellationToken.None));

        Assert.Equal(2, restored.Count);
        Assert.All(restored, snapshot => Assert.Equal(interruptedRun.RunId, snapshot.RunId));
        Assert.DoesNotContain(restored, snapshot => snapshot.PayloadJson.Contains("clean-run", StringComparison.Ordinal));

        // Restoring reads; it does not consume. The next run has the same
        // session to come back to if this one never gets to save its own.
        Assert.Equal(
            2,
            Success(await restore.LoadLatestSessionAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task DiscardDeletesOnlyInterruptedRuntimeStateAndKeepsDefinitions()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        var definition = DurableDefinitionFixtures.Layout();
        Assert.True((await repository.SaveAsync(definition, null, CancellationToken.None)).IsSuccess);
        var runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        var recoveryStore = new SqliteRuntimeRecoveryStore(temporary.Database);
        var interruptedRun = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(interruptedRun.RunId, "window-one", "interrupted-run"),
            CancellationToken.None)).IsSuccess);
        await temporary.ReopenAsync();

        runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        recoveryStore = new SqliteRuntimeRecoveryStore(temporary.Database);
        _ = Success(await runStore.BeginRunAsync(CancellationToken.None));

        var discarded = await recoveryStore.DiscardAsync(
            interruptedRun.RunId,
            CancellationToken.None);

        Assert.True(discarded.IsSuccess);
        Assert.Empty(Success(await recoveryStore.LoadAsync(
            interruptedRun.RunId,
            CancellationToken.None)));
        repository = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        Assert.True((await repository.GetAsync(definition.Key, CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task SnapshotSaveRejectsAWriterFromAnyRunExceptTheActiveRun()
    {
        await using var temporary = TemporaryDatabase.Create();
        var runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        var recoveryStore = new SqliteRuntimeRecoveryStore(temporary.Database);
        var firstRun = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.True((await runStore.CompleteRunAsync(
            firstRun.RunId,
            CancellationToken.None)).IsSuccess);
        _ = Success(await runStore.BeginRunAsync(CancellationToken.None));

        var lateSave = await recoveryStore.SaveAsync(
            Snapshot(firstRun.RunId, "window-one", "late-write"),
            CancellationToken.None);

        Assert.False(lateSave.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.RunMismatch, lateSave.Error!.Code);
    }

    [Fact]
    public async Task RecoveryInventoryIsBoundedGroupedAndExcludesTheActiveRun()
    {
        await using var temporary = TemporaryDatabase.Create();
        var runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        var recoveryStore = new SqliteRuntimeRecoveryStore(temporary.Database);
        var first = Success(await runStore.BeginRunAsync(CancellationToken.None));
        var firstPayload = """{"window":"first"}""";
        var firstUpdated = new DateTimeOffset(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(first.RunId, "window-one", firstPayload, firstUpdated),
            CancellationToken.None)).IsSuccess);
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(first.RunId, "window-two", "{}", firstUpdated.AddMinutes(1)),
            CancellationToken.None)).IsSuccess);
        Assert.True((await runStore.CompleteRunAsync(
            first.RunId,
            CancellationToken.None)).IsSuccess);

        var second = Success(await runStore.BeginRunAsync(CancellationToken.None));
        var secondPayload = """{"window":"second"}""";
        var secondUpdated = firstUpdated.AddHours(1);
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(second.RunId, "window-one", secondPayload, secondUpdated),
            CancellationToken.None)).IsSuccess);
        Assert.True((await runStore.CompleteRunAsync(
            second.RunId,
            CancellationToken.None)).IsSuccess);

        var active = Success(await runStore.BeginRunAsync(CancellationToken.None));
        recoveryStore = new SqliteRuntimeRecoveryStore(
            temporary.Database,
            InitializeStartup(active));
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(active.RunId, "window-one", """{"window":"active"}""", secondUpdated.AddHours(1)),
            CancellationToken.None)).IsSuccess);

        var inventory = Success(await recoveryStore.ListAsync(CancellationToken.None));

        Assert.Equal(2, inventory.ListedRunCount);
        Assert.Equal(3, inventory.ListedSnapshotCount);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(firstPayload)
                + Encoding.UTF8.GetByteCount("{}")
                + Encoding.UTF8.GetByteCount(secondPayload),
            inventory.ListedPayloadBytes);
        Assert.False(inventory.IsTruncated);
        Assert.Equal([second.RunId, first.RunId], inventory.Runs.Select(item => item.RunId), StringComparer.Ordinal);
        Assert.Equal(secondUpdated, inventory.Runs[0].LastUpdatedAt);
        Assert.Equal(2, inventory.Runs[1].SnapshotCount);
        Assert.DoesNotContain(inventory.Runs, item => string.Equals(item.RunId, active.RunId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoveryDataControlClearsOnlyInactiveRunsAndKeepsDefinitions()
    {
        await using var temporary = TemporaryDatabase.Create();
        var repository = new SqliteDefinitionRepository<LayoutDefinition>(
            temporary.Database,
            TimeProvider.System);
        var definition = DurableDefinitionFixtures.Layout();
        Assert.True((await repository.SaveAsync(
            definition,
            null,
            CancellationToken.None)).IsSuccess);
        var runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        var recoveryStore = new SqliteRuntimeRecoveryStore(temporary.Database);
        var first = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(first.RunId, "window-one", "{}", DateTimeOffset.UtcNow),
            CancellationToken.None)).IsSuccess);
        Assert.True((await runStore.CompleteRunAsync(
            first.RunId,
            CancellationToken.None)).IsSuccess);
        var second = Success(await runStore.BeginRunAsync(CancellationToken.None));
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(second.RunId, "window-one", "{}", DateTimeOffset.UtcNow),
            CancellationToken.None)).IsSuccess);
        Assert.True((await runStore.CompleteRunAsync(
            second.RunId,
            CancellationToken.None)).IsSuccess);
        var active = Success(await runStore.BeginRunAsync(CancellationToken.None));
        recoveryStore = new SqliteRuntimeRecoveryStore(
            temporary.Database,
            InitializeStartup(active));
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(active.RunId, "window-one", "{}", DateTimeOffset.UtcNow),
            CancellationToken.None)).IsSuccess);

        Assert.Equal(1, Success(await recoveryStore.DiscardRunAsync(
            first.RunId,
            CancellationToken.None)));
        Assert.Empty(Success(await recoveryStore.LoadAsync(
            first.RunId,
            CancellationToken.None)));
        Assert.Equal(1, Success(await recoveryStore.DiscardAllAsync(
            CancellationToken.None)));
        var activeDiscard = await recoveryStore.DiscardRunAsync(
            active.RunId,
            CancellationToken.None);
        var legacyActiveDiscard = await recoveryStore.DiscardAsync(
            active.RunId,
            CancellationToken.None);
        Assert.Equal(ApplicationRunErrorCode.RunMismatch, activeDiscard.Error!.Code);
        Assert.Equal(ApplicationRunErrorCode.RunMismatch, legacyActiveDiscard.Error!.Code);
        Assert.Empty(Success(await recoveryStore.ListAsync(
            CancellationToken.None)).Runs);
        Assert.Single(Success(await recoveryStore.LoadAsync(
            active.RunId,
            CancellationToken.None)));
        Assert.True((await repository.GetAsync(
            definition.Key,
            CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task RecoveryInventoryReportsTruncationWithoutReadingPayloads()
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        var active = Success(await new SqliteApplicationRunStore(
            temporary.Database,
            TimeProvider.System).BeginRunAsync(CancellationToken.None));
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        await using (var transaction = connection.BeginTransaction())
        {
            for (var index = 0;
                 index < RuntimeRecoveryInventory.MaximumListedRuns + 1;
                 index++)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO runtime_snapshots(
                        run_id,
                        snapshot_key,
                        schema_version,
                        payload_json,
                        updated_utc)
                    VALUES ($runId, 'desktop.main-window', 1, '{}', $updatedUtc);
                    """;
                command.Parameters.AddWithValue(
                    "$runId",
                    $"inactive-{index:D3}");
                command.Parameters.AddWithValue(
                    "$updatedUtc",
                    DateTimeOffset.UnixEpoch
                        .AddMinutes(index)
                        .ToString("O", CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        var inventory = Success(await new SqliteRuntimeRecoveryStore(
            temporary.Database,
            InitializeStartup(active)).ListAsync(CancellationToken.None));

        Assert.Equal(RuntimeRecoveryInventory.MaximumListedRuns, inventory.Runs.Count);
        Assert.Equal(RuntimeRecoveryInventory.MaximumListedRuns, inventory.ListedRunCount);
        Assert.Equal(RuntimeRecoveryInventory.MaximumListedRuns, inventory.ListedSnapshotCount);
        Assert.True(inventory.IsTruncated);
        Assert.Equal("inactive-100", inventory.Runs[0].RunId);
    }

    [Fact]
    public async Task RecoveryDataControlReturnsTypedCancellationAndRejectsInvalidMetadata()
    {
        await using var temporary = TemporaryDatabase.Create();
        var active = Success(await new SqliteApplicationRunStore(
            temporary.Database,
            TimeProvider.System).BeginRunAsync(CancellationToken.None));
        var recoveryStore = new SqliteRuntimeRecoveryStore(
            temporary.Database,
            InitializeStartup(active));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var list = await recoveryStore.ListAsync(cancellation.Token);
        var discardAll = await recoveryStore.DiscardAllAsync(cancellation.Token);
        var invalidRun = await recoveryStore.DiscardRunAsync(
            "\n",
            CancellationToken.None);

        Assert.Equal(ApplicationRunErrorCode.Cancelled, list.Error!.Code);
        Assert.Equal(ApplicationRunErrorCode.Cancelled, discardAll.Error!.Code);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, invalidRun.Error!.Code);
    }

    [Fact]
    public async Task RecoveryDataControlFailsClosedWithoutAValidActiveLifecycle()
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM app_lifecycle;
                INSERT INTO runtime_snapshots(
                    run_id,
                    snapshot_key,
                    schema_version,
                    payload_json,
                    updated_utc)
                VALUES (
                    'inactive-run',
                    'desktop.main-window',
                    1,
                    '{}',
                    '2026-07-23T08:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var startupState = InitializeStartup(new ApplicationRunStart(
            "current-run",
            RecoveryRequired: false,
            new ApplicationRunState(null, WasClean: true, null, null)));
        var recoveryStore = new SqliteRuntimeRecoveryStore(
            temporary.Database,
            startupState);
        var inventory = await recoveryStore.ListAsync(CancellationToken.None);
        var discard = await recoveryStore.DiscardAllAsync(CancellationToken.None);

        Assert.Equal(ApplicationRunErrorCode.StorageFailure, inventory.Error!.Code);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, discard.Error!.Code);
        Assert.Single(Success(await recoveryStore.LoadAsync(
            "inactive-run",
            CancellationToken.None)));
    }

    [Fact]
    public async Task RecoveryDeletionFailsClosedWhenLifecycleDoesNotMatchThisProcess()
    {
        await using var temporary = TemporaryDatabase.Create();
        var runStore = new SqliteApplicationRunStore(temporary.Database, TimeProvider.System);
        var inactive = Success(await runStore.BeginRunAsync(CancellationToken.None));
        var setupStore = new SqliteRuntimeRecoveryStore(temporary.Database);
        Assert.True((await setupStore.SaveAsync(
            Snapshot(inactive.RunId, "inactive-window", "inactive"),
            CancellationToken.None)).IsSuccess);
        Assert.True((await runStore.CompleteRunAsync(
            inactive.RunId,
            CancellationToken.None)).IsSuccess);
        var active = Success(await runStore.BeginRunAsync(CancellationToken.None));
        var recoveryStore = new SqliteRuntimeRecoveryStore(
            temporary.Database,
            InitializeStartup(active));
        Assert.True((await recoveryStore.SaveAsync(
            Snapshot(active.RunId, "active-window", "active"),
            CancellationToken.None)).IsSuccess);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE app_lifecycle
                SET current_run_id = 'different-valid-run'
                WHERE singleton_id = 1;
                """;
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var inventory = await recoveryStore.ListAsync(CancellationToken.None);
        var discardAll = await recoveryStore.DiscardAllAsync(CancellationToken.None);
        var legacyDiscard = await recoveryStore.DiscardAsync(
            inactive.RunId,
            CancellationToken.None);

        Assert.Equal(ApplicationRunErrorCode.StorageFailure, inventory.Error!.Code);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, discardAll.Error!.Code);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, legacyDiscard.Error!.Code);
        Assert.Single(Success(await recoveryStore.LoadAsync(
            inactive.RunId,
            CancellationToken.None)));
        Assert.Single(Success(await recoveryStore.LoadAsync(
            active.RunId,
            CancellationToken.None)));
    }

    [Fact]
    public async Task RecoveryReadsFailClosedWhenARunExceedsItsSnapshotBound()
    {
        await using var temporary = TemporaryDatabase.Create();
        var active = Success(await new SqliteApplicationRunStore(
            temporary.Database,
            TimeProvider.System).BeginRunAsync(CancellationToken.None));
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        await using (var transaction = connection.BeginTransaction())
        {
            for (var index = 0;
                 index <= RuntimeRecoveryInventory.MaximumSnapshotsPerRun;
                 index++)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO runtime_snapshots(
                        run_id,
                        snapshot_key,
                        schema_version,
                        payload_json,
                        updated_utc)
                    VALUES (
                        'oversized-run',
                        $snapshotKey,
                        1,
                        '{}',
                        $updatedUtc);
                    """;
                command.Parameters.AddWithValue("$snapshotKey", $"snapshot-{index:D3}");
                command.Parameters.AddWithValue(
                    "$updatedUtc",
                    DateTimeOffset.UnixEpoch
                        .AddMinutes(index)
                        .ToString("O", CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        var recoveryStore = new SqliteRuntimeRecoveryStore(
            temporary.Database,
            InitializeStartup(active));

        var load = await recoveryStore.LoadAsync(
            "oversized-run",
            CancellationToken.None);
        var inventory = await recoveryStore.ListAsync(CancellationToken.None);

        Assert.Equal(ApplicationRunErrorCode.StorageFailure, load.Error!.Code);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, inventory.Error!.Code);
    }

    [Fact]
    public async Task RecoverySaveBoundsNewSnapshotKeysButAllowsAnExistingKeyUpdate()
    {
        await using var temporary = TemporaryDatabase.Create();
        var active = Success(await new SqliteApplicationRunStore(
            temporary.Database,
            TimeProvider.System).BeginRunAsync(CancellationToken.None));
        var recoveryStore = new SqliteRuntimeRecoveryStore(
            temporary.Database,
            InitializeStartup(active));
        for (var index = 0;
             index < RuntimeRecoveryInventory.MaximumSnapshotsPerRun;
             index++)
        {
            var saved = await recoveryStore.SaveAsync(
                Snapshot(
                    active.RunId,
                    $"snapshot-{index:D3}",
                    "{}",
                    DateTimeOffset.UnixEpoch.AddMinutes(index)),
                CancellationToken.None);
            Assert.True(saved.IsSuccess, saved.Error?.Message);
        }

        var overflow = await recoveryStore.SaveAsync(
            Snapshot(
                active.RunId,
                "snapshot-overflow",
                "{}",
                DateTimeOffset.UnixEpoch.AddDays(1)),
            CancellationToken.None);
        var update = await recoveryStore.SaveAsync(
            Snapshot(
                active.RunId,
                "snapshot-000",
                """{"updated":true}""",
                DateTimeOffset.UnixEpoch.AddDays(2)),
            CancellationToken.None);

        Assert.Equal(ApplicationRunErrorCode.StorageFailure, overflow.Error!.Code);
        Assert.True(update.IsSuccess, update.Error?.Message);
        Assert.Equal(
            RuntimeRecoveryInventory.MaximumSnapshotsPerRun,
            Success(await recoveryStore.LoadAsync(
                active.RunId,
                CancellationToken.None)).Count);
    }

    [Fact]
    public async Task RecoveryStoreMapsDisposedDatabaseToTypedStorageFailure()
    {
        await using var temporary = TemporaryDatabase.Create();
        var active = Success(await new SqliteApplicationRunStore(
            temporary.Database,
            TimeProvider.System).BeginRunAsync(CancellationToken.None));
        var recoveryStore = new SqliteRuntimeRecoveryStore(
            temporary.Database,
            InitializeStartup(active));
        await temporary.Database.DisposeAsync();

        var inventory = await recoveryStore.ListAsync(CancellationToken.None);
        var discard = await recoveryStore.DiscardAllAsync(CancellationToken.None);
        var load = await recoveryStore.LoadAsync("inactive-run", CancellationToken.None);

        Assert.Equal(ApplicationRunErrorCode.StorageFailure, inventory.Error!.Code);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, discard.Error!.Code);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, load.Error!.Code);
    }

    [Fact]
    public void StartupStateRequiresTheInterruptedRunToBeNamed()
    {
        var previous = new ApplicationRunState(
            RunId: null,
            WasClean: false,
            DateTimeOffset.UtcNow,
            LastCleanAt: null);

        Assert.Throws<ArgumentException>(() => InitializeStartup(new ApplicationRunStart(
            "current-run",
            RecoveryRequired: true,
            previous)));
    }

    /// <summary>
    /// The startup path itself: the preference, the store, and the inventory the
    /// newest stored session is picked from — the same three the desktop
    /// composes, so these tests exercise what actually opens the window.
    /// </summary>
    private static SessionRestoreCoordinator StartupRestore(
        TemporaryDatabase temporary,
        ApplicationRunStart currentRun)
    {
        var store = new SqliteRuntimeRecoveryStore(
            temporary.Database,
            InitializeStartup(currentRun));
        return new SessionRestoreCoordinator(
            new SqliteSessionRestorePreferenceStore(temporary.Database),
            store,
            store);
    }

    private static ApplicationStartupState InitializeStartup(ApplicationRunStart run)
    {
        var startupState = new ApplicationStartupState();
        startupState.Initialize(run);
        return startupState;
    }

    private static RuntimeRecoverySnapshot Snapshot(string runId, string key, string owner) => new(
        runId,
        key,
        1,
        $"{{\"owner\":\"{owner}\"}}",
        DateTimeOffset.UtcNow);

    private static RuntimeRecoverySnapshot Snapshot(
        string runId,
        string key,
        string payload,
        DateTimeOffset updatedAt) => new(
        runId,
        key,
        1,
        payload,
        updatedAt);

    private static T Success<T>(ApplicationRunResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }
}
