using Asura.Application;

namespace Asura.Application.Tests;

public sealed class RuntimeRecoveryDataControlContractsTests
{
    [Fact]
    public void InventoryCopiesItsInputAndDerivesOnlyListedTotals()
    {
        var source = new[]
        {
            Summary("run-one", snapshotCount: 2, payloadBytes: 12),
            Summary("run-two", snapshotCount: 1, payloadBytes: 5),
        };

        var inventory = new RuntimeRecoveryInventory(source, hasAdditionalRuns: false);
        source[0] = Summary("mutated", snapshotCount: 1, payloadBytes: 1);

        Assert.Equal(["run-one", "run-two"], inventory.Runs.Select(item => item.RunId), StringComparer.Ordinal);
        Assert.Equal(2, inventory.ListedRunCount);
        Assert.Equal(3, inventory.ListedSnapshotCount);
        Assert.Equal(17, inventory.ListedPayloadBytes);
        Assert.False(inventory.HasAdditionalRuns);
    }

    [Fact]
    public void TruncationRequiresACompleteBoundedPage()
    {
        var runs = Enumerable
            .Range(0, RuntimeRecoveryInventory.MaximumListedRuns - 1)
            .Select(index => Summary($"run-{index:D3}", 1, 2))
            .ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RuntimeRecoveryInventory(runs, hasAdditionalRuns: true));
    }

    [Fact]
    public void SummaryRejectsMoreThanThePerRunSnapshotLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Summary(
            "run-one",
            RuntimeRecoveryInventory.MaximumSnapshotsPerRun + 1L,
            payloadBytes: 2));
    }

    [Fact]
    public void InventoryRejectsDuplicateRunIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => new RuntimeRecoveryInventory(
            [
                Summary("run-one", 1, 2),
                Summary("run-one", 1, 2),
            ],
            hasAdditionalRuns: false));
    }

    private static RuntimeRecoveryRunSummary Summary(
        string runId,
        long snapshotCount,
        long payloadBytes) =>
        new(
            runId,
            snapshotCount,
            payloadBytes,
            DateTimeOffset.UnixEpoch);
}
