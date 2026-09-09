using Asura.Application;

namespace Asura.Infrastructure.Tests;

public sealed class SqliteOnboardingProgressStoreTests
{
    [Fact]
    public async Task FreshProfileStartsIncompleteAndCompletionSurvivesRestart()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteOnboardingProgressStore(temporary.Database);

        var initial = Success(await store.ReadAsync(CancellationToken.None));
        var completed = Success(await store.CompleteAsync(
            version: 1,
            initial.Revision,
            CancellationToken.None));

        Assert.Equal(0, initial.CompletedVersion);
        Assert.Equal(1, initial.Revision);
        Assert.Equal(1, completed.CompletedVersion);
        Assert.Equal(2, completed.Revision);

        await temporary.ReopenAsync();
        store = new SqliteOnboardingProgressStore(temporary.Database);
        Assert.Equal(completed, Success(await store.ReadAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task CompletionIsIdempotentButAStaleAdvanceConflicts()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteOnboardingProgressStore(temporary.Database);
        var initial = Success(await store.ReadAsync(CancellationToken.None));
        var completed = Success(await store.CompleteAsync(
            version: 1,
            initial.Revision,
            CancellationToken.None));

        var idempotent = await store.CompleteAsync(
            version: 1,
            initial.Revision,
            CancellationToken.None);
        var staleAdvance = await store.CompleteAsync(
            version: 2,
            initial.Revision,
            CancellationToken.None);

        Assert.True(idempotent.IsSuccess, idempotent.Error?.Message);
        Assert.Equal(completed, idempotent.Value);
        Assert.Equal(OnboardingProgressErrorCode.Conflict, staleAdvance.Error!.Code);
        Assert.Equal(completed, Success(await store.ReadAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task CancelledReadAndCompletionReturnTypedCancellation()
    {
        await using var temporary = TemporaryDatabase.Create();
        var store = new SqliteOnboardingProgressStore(temporary.Database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var read = await store.ReadAsync(cancellation.Token);
        var complete = await store.CompleteAsync(1, 1, cancellation.Token);

        Assert.Equal(OnboardingProgressErrorCode.Cancelled, read.Error!.Code);
        Assert.Equal(OnboardingProgressErrorCode.Cancelled, complete.Error!.Code);
    }

    [Fact]
    public async Task InvalidStorageTypesFailClosed()
    {
        await using var temporary = TemporaryDatabase.Create();
        await temporary.Database.EnsureInitializedAsync(CancellationToken.None);
        await using (var connection = await temporary.Database.OpenConnectionAsync(
            CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA ignore_check_constraints = ON;
                UPDATE onboarding_progress
                SET completed_version = 'invalid';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var result = await new SqliteOnboardingProgressStore(temporary.Database)
            .ReadAsync(CancellationToken.None);

        Assert.Equal(OnboardingProgressErrorCode.InvalidData, result.Error!.Code);
    }

    private static OnboardingProgress Success(
        OnboardingProgressResult<OnboardingProgress> result)
    {
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }
}
