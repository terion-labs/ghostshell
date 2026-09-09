using Asura.App.ViewModels;
using Asura.Application;

namespace Asura.App.Tests;

public sealed class RecoveryDataControlViewModelTests
{
    [Fact]
    public async Task StartLoadsMetadataOnlyPreviousRunSummaries()
    {
        var source = new RecordingRecoveryDataControl();
        source.ListResults.Enqueue(Success(new RuntimeRecoveryInventory(
            [
                new RuntimeRecoveryRunSummary(
                    "private-run-id",
                    2,
                    1536,
                    new DateTimeOffset(2026, 7, 23, 9, 30, 0, TimeSpan.Zero)),
            ],
            hasAdditionalRuns: false)));
        using var viewModel = new RecoveryDataControlViewModel(source);

        viewModel.Start();
        await viewModel.Initialization;

        var item = Assert.Single(viewModel.Runs);
        Assert.Equal("Saved recovery 1", item.Title);
        Assert.Equal("Clear saved recovery 1", item.ClearAutomationName);
        Assert.Equal("2 snapshots", item.SnapshotLabel);
        Assert.Equal("1.5 KiB", item.SizeLabel);
        Assert.Equal("Last saved 2026-07-23 09:30 UTC", item.UpdatedLabel);
        Assert.DoesNotContain(
            "private-run-id",
            string.Join(' ', item.Title, item.SnapshotLabel, item.SizeLabel, item.UpdatedLabel),
            StringComparison.Ordinal);
        Assert.Equal("2 snapshots from 1 previous run.", viewModel.StatusMessage);
        Assert.True(viewModel.HasRuns);
        Assert.True(viewModel.CanClearAll);
        Assert.False(viewModel.HasError);
    }

    [Fact]
    public async Task DiscardRunUsesOpaqueIdentityThenRefreshesTheInventory()
    {
        var source = new RecordingRecoveryDataControl();
        source.ListResults.Enqueue(Success(Inventory("run-to-clear")));
        source.ListResults.Enqueue(Success(EmptyInventory()));
        source.DiscardRunResult = Success(2L);
        using var viewModel = new RecoveryDataControlViewModel(source);
        viewModel.Start();
        await viewModel.Initialization;

        await viewModel.DiscardRunAsync(Assert.Single(viewModel.Runs));

        Assert.Equal("run-to-clear", source.DiscardedRunId);
        Assert.Equal(2, source.ListCalls);
        Assert.Empty(viewModel.Runs);
        Assert.True(viewModel.HasNoRuns);
        Assert.Equal("Cleared 2 snapshots.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SuccessfulDeleteFollowedByRefreshFailureReportsBothFacts()
    {
        var source = new RecordingRecoveryDataControl();
        source.ListResults.Enqueue(Success(Inventory("run-to-clear")));
        source.ListResults.Enqueue(Failure<RuntimeRecoveryInventory>(
            ApplicationRunErrorCode.StorageUnavailable,
            "Unavailable"));
        source.DiscardAllResult = Success(1L);
        using var viewModel = new RecoveryDataControlViewModel(source);
        viewModel.Start();
        await viewModel.Initialization;

        await viewModel.DiscardAllAsync();

        Assert.True(viewModel.HasError);
        Assert.Empty(viewModel.Runs);
        Assert.False(viewModel.HasRuns);
        Assert.False(viewModel.HasNoRuns);
        Assert.Equal(0, viewModel.ListedRunCount);
        Assert.Equal(0, viewModel.ListedSnapshotCount);
        Assert.Equal(0, viewModel.ListedPayloadBytes);
        Assert.False(viewModel.HasAdditionalRuns);
        Assert.StartsWith("Cleared 1 snapshot.", viewModel.StatusMessage);
        Assert.Contains(
            "remaining recovery metadata could not be loaded",
            viewModel.StatusMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            nameof(ApplicationRunErrorCode.StorageUnavailable),
            viewModel.StatusMessage,
            StringComparison.Ordinal);
        Assert.False(viewModel.CanClearAll);
    }

    [Fact]
    public async Task RowAutomationNamesAreSafeUniqueAndDoNotExposeRunIds()
    {
        var source = new RecordingRecoveryDataControl();
        source.ListResults.Enqueue(Success(new RuntimeRecoveryInventory(
            [
                new RuntimeRecoveryRunSummary(
                    "private-run-alpha",
                    1,
                    2,
                    DateTimeOffset.UnixEpoch),
                new RuntimeRecoveryRunSummary(
                    "private-run-beta",
                    1,
                    2,
                    DateTimeOffset.UnixEpoch),
            ],
            hasAdditionalRuns: false)));
        using var viewModel = new RecoveryDataControlViewModel(source);

        viewModel.Start();
        await viewModel.Initialization;

        Assert.Equal(
            ["Clear saved recovery 1", "Clear saved recovery 2"],
            viewModel.Runs.Select(item => item.ClearAutomationName), StringComparer.Ordinal);
        Assert.All(
            viewModel.Runs,
            item =>
            {
                Assert.DoesNotContain(
                    "private-run-alpha",
                    item.ClearAutomationName,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "private-run-beta",
                    item.ClearAutomationName,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task RefreshExceptionAfterDeleteKeepsTheMutationReceiptAndClearsStaleState()
    {
        var source = new RecordingRecoveryDataControl
        {
            ListException = new IOException("private-path-canary"),
            ThrowOnListCall = 2,
            DiscardAllResult = Success(1L),
        };
        source.ListResults.Enqueue(Success(Inventory("run-to-clear")));
        using var viewModel = new RecoveryDataControlViewModel(source);
        viewModel.Start();
        await viewModel.Initialization;

        await viewModel.DiscardAllAsync();

        Assert.True(viewModel.HasError);
        Assert.Empty(viewModel.Runs);
        Assert.Equal(0, viewModel.ListedRunCount);
        Assert.StartsWith("Cleared 1 snapshot.", viewModel.StatusMessage);
        Assert.Contains(
            nameof(ApplicationRunErrorCode.StorageFailure),
            viewModel.StatusMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "private-path-canary",
            viewModel.StatusMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureFailsClosedAndTruncatedInventoryIsDisclosed()
    {
        var source = new RecordingRecoveryDataControl();
        source.ListResults.Enqueue(Failure<RuntimeRecoveryInventory>(
            ApplicationRunErrorCode.StorageFailure,
            "Unsafe details must not be shown."));
        source.ListResults.Enqueue(Success(new RuntimeRecoveryInventory(
            [.. Enumerable.Range(1, RuntimeRecoveryInventory.MaximumListedRuns)
                .Select(index => new RuntimeRecoveryRunSummary(
                    $"run-{index}",
                    1,
                    2,
                    DateTimeOffset.UnixEpoch))],
            hasAdditionalRuns: true)));
        using var viewModel = new RecoveryDataControlViewModel(source);
        viewModel.Start();
        await viewModel.Initialization;

        Assert.True(viewModel.HasError);
        Assert.Empty(viewModel.Runs);
        Assert.DoesNotContain(
            "Unsafe details",
            viewModel.StatusMessage,
            StringComparison.Ordinal);

        await viewModel.RefreshAsync();

        Assert.False(viewModel.HasError);
        Assert.True(viewModel.HasAdditionalRuns);
        Assert.Contains(
            $"newest {RuntimeRecoveryInventory.MaximumListedRuns} previous runs",
            viewModel.StatusMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            "Older recovery runs are also stored.",
            viewModel.StatusMessage,
            StringComparison.Ordinal);
    }

    private static RuntimeRecoveryInventory Inventory(string runId) => new(
        [
            new RuntimeRecoveryRunSummary(
                runId,
                1,
                2,
                DateTimeOffset.UnixEpoch),
        ],
        hasAdditionalRuns: false);

    private static RuntimeRecoveryInventory EmptyInventory() => new(
        [],
        hasAdditionalRuns: false);

    private static ApplicationRunResult<T> Success<T>(T value) =>
        ApplicationRunResult<T>.Success(value);

    private static ApplicationRunResult<T> Failure<T>(
        ApplicationRunErrorCode code,
        string message) =>
        ApplicationRunResult<T>.Failure(new ApplicationRunError(code, message));

    private sealed class RecordingRecoveryDataControl : IRuntimeRecoveryDataControl
    {
        public Queue<ApplicationRunResult<RuntimeRecoveryInventory>> ListResults { get; } = [];

        public ApplicationRunResult<long> DiscardRunResult { get; set; } = Success(0L);

        public ApplicationRunResult<long> DiscardAllResult { get; set; } = Success(0L);

        public int ListCalls { get; private set; }

        public Exception? ListException { get; set; }

        public int ThrowOnListCall { get; set; }

        public string? DiscardedRunId { get; private set; }

        public ValueTask<ApplicationRunResult<RuntimeRecoveryInventory>> ListAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListCalls++;
            if (ListCalls == ThrowOnListCall)
            {
                throw ListException
                    ?? new InvalidOperationException("A configured list exception is missing.");
            }

            return ValueTask.FromResult(ListResults.Dequeue());
        }

        public ValueTask<ApplicationRunResult<long>> DiscardRunAsync(
            string runId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscardedRunId = runId;
            return ValueTask.FromResult(DiscardRunResult);
        }

        public ValueTask<ApplicationRunResult<long>> DiscardAllAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(DiscardAllResult);
        }
    }
}
