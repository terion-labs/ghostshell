using Asura.Application;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteApplicationRunStoreTests
{
    [Fact]
    public async Task ProfileLockContentionIsTypedAndDoesNotReplaceTheActiveRun()
    {
        await using var temporary = TemporaryDatabase.Create();
        var primaryStore = new SqliteApplicationRunStore(
            temporary.Database,
            TimeProvider.System);
        var primaryRun = Success(await primaryStore.BeginRunAsync(CancellationToken.None));
        await using var competingDatabase = new AsuraDatabase(
            new SqliteStorageOptions(temporary.DatabasePath),
            TimeProvider.System);
        var competingStore = new SqliteApplicationRunStore(
            competingDatabase,
            TimeProvider.System);

        var contention = await competingStore.BeginRunAsync(CancellationToken.None);

        Assert.False(contention.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageUnavailable, contention.Error!.Code);
        var unchanged = Success(await primaryStore.GetStateAsync(CancellationToken.None));
        Assert.Equal(primaryRun.RunId, unchanged.RunId);
        Assert.False(unchanged.WasClean);

        await temporary.Database.DisposeAsync();
        var recovered = Success(await competingStore.BeginRunAsync(CancellationToken.None));

        Assert.True(recovered.RecoveryRequired);
        Assert.Equal(primaryRun.RunId, recovered.PreviousState.RunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidTimestampIsTypedAndLeavesTheMarkerUnchanged(
        bool invalidStartedTimestamp)
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        var marker = invalidStartedTimestamp
            ? new RawLifecycleMarker(
                WasClean: false,
                RunId: "interrupted-run",
                StartedUtc: "not-a-timestamp",
                LastCleanUtc: "2026-07-22T12:00:00.0000000+00:00")
            : new RawLifecycleMarker(
                WasClean: true,
                RunId: null,
                StartedUtc: null,
                LastCleanUtc: "not-a-timestamp");
        await WriteRawMarkerAsync(temporary.Database, marker);
        var store = new SqliteApplicationRunStore(
            temporary.Database,
            TimeProvider.System);

        var result = await store.BeginRunAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, result.Error!.Code);
        Assert.Equal(marker, await ReadRawMarkerAsync(temporary.Database));
    }

    [Fact]
    public async Task BlankDirtyRunIdIsTypedAndLeavesTheMarkerUnchanged()
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        var marker = new RawLifecycleMarker(
            WasClean: false,
            RunId: " ",
            StartedUtc: "2026-07-22T12:00:00.0000000+00:00",
            LastCleanUtc: null);
        await WriteRawMarkerAsync(temporary.Database, marker);
        var store = new SqliteApplicationRunStore(
            temporary.Database,
            TimeProvider.System);

        var result = await store.BeginRunAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, result.Error!.Code);
        Assert.Equal(marker, await ReadRawMarkerAsync(temporary.Database));
    }

    [Theory]
    [InlineData("run-id")]
    [InlineData("started")]
    [InlineData("last-clean")]
    public async Task InvalidStorageTypeIsTypedAndLeavesTheMarkerUnchanged(string field)
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await WriteInvalidStorageTypeAsync(temporary.Database, field);
        var before = await ReadStorageSignatureAsync(temporary.Database);
        var store = new SqliteApplicationRunStore(
            temporary.Database,
            TimeProvider.System);

        var result = await store.BeginRunAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplicationRunErrorCode.StorageFailure, result.Error!.Code);
        Assert.Equal(before, await ReadStorageSignatureAsync(temporary.Database));
    }

    private static async Task WriteRawMarkerAsync(
        AsuraDatabase database,
        RawLifecycleMarker marker)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE app_lifecycle
            SET clean_shutdown = $wasClean,
                current_run_id = $runId,
                started_utc = $startedUtc,
                last_clean_utc = $lastCleanUtc
            WHERE singleton_id = 1;
            """;
        command.Parameters.AddWithValue("$wasClean", marker.WasClean);
        command.Parameters.AddWithValue("$runId", (object?)marker.RunId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$startedUtc",
            (object?)marker.StartedUtc ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$lastCleanUtc",
            (object?)marker.LastCleanUtc ?? DBNull.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<RawLifecycleMarker> ReadRawMarkerAsync(
        AsuraDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT clean_shutdown, current_run_id, started_utc, last_clean_utc
            FROM app_lifecycle
            WHERE singleton_id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new RawLifecycleMarker(
            reader.GetBoolean(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task WriteInvalidStorageTypeAsync(
        AsuraDatabase database,
        string field)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = field switch
        {
            "run-id" => """
                UPDATE app_lifecycle
                SET clean_shutdown = 0,
                    current_run_id = X'0102',
                    started_utc = '2026-07-22T12:00:00.0000000+00:00',
                    last_clean_utc = NULL
                WHERE singleton_id = 1;
                """,
            "started" => """
                UPDATE app_lifecycle
                SET clean_shutdown = 0,
                    current_run_id = 'interrupted-run',
                    started_utc = X'0102',
                    last_clean_utc = NULL
                WHERE singleton_id = 1;
                """,
            "last-clean" => """
                UPDATE app_lifecycle
                SET clean_shutdown = 1,
                    current_run_id = NULL,
                    started_utc = NULL,
                    last_clean_utc = X'0102'
                WHERE singleton_id = 1;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<string> ReadStorageSignatureAsync(
        AsuraDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT printf(
                '%s:%s|%s:%s|%s:%s|%s:%s',
                typeof(clean_shutdown), quote(clean_shutdown),
                typeof(current_run_id), quote(current_run_id),
                typeof(started_utc), quote(started_utc),
                typeof(last_clean_utc), quote(last_clean_utc))
            FROM app_lifecycle
            WHERE singleton_id = 1;
            """;
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static T Success<T>(ApplicationRunResult<T> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    private sealed record RawLifecycleMarker(
        bool WasClean,
        string? RunId,
        string? StartedUtc,
        string? LastCleanUtc);
}
