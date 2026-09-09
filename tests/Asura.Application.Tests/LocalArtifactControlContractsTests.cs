using Asura.Application;

namespace Asura.Application.Tests;

public sealed class LocalArtifactControlContractsTests
{
    [Fact]
    public void InventorySnapshotsTheExactArtifactCategorySet()
    {
        var source = new List<LocalArtifactSummary>
        {
            new(LocalArtifactKind.Cache, 2, 30),
            new(LocalArtifactKind.InactiveApplicationLogs, 1, 20),
        };

        var inventory = new LocalArtifactInventory(source);
        source.Clear();

        Assert.Equal(2, inventory.Artifacts.Count);
        Assert.Equal(LocalArtifactKind.Cache, inventory.Artifacts[0].Kind);
        Assert.Equal(
            LocalArtifactKind.InactiveApplicationLogs,
            inventory.Artifacts[1].Kind);
    }

    [Fact]
    public void InventoryRejectsMissingDuplicateAndUndefinedCategories()
    {
        Assert.Throws<ArgumentException>(() => new LocalArtifactInventory(
            [
                new LocalArtifactSummary(LocalArtifactKind.Cache, 0, 0),
            ]));
        Assert.Throws<ArgumentException>(() => new LocalArtifactInventory(
            [
                new LocalArtifactSummary(LocalArtifactKind.Cache, 0, 0),
                new LocalArtifactSummary(LocalArtifactKind.Cache, 0, 0),
            ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalArtifactInventory(
            [
                new LocalArtifactSummary(LocalArtifactKind.Cache, 0, 0),
                new LocalArtifactSummary((LocalArtifactKind)999, 0, 0),
            ]));
    }

    [Fact]
    public void CountsAndBytesCannotBeNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalArtifactSummary(LocalArtifactKind.Cache, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalArtifactSummary(LocalArtifactKind.Cache, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalArtifactClearReceipt(LocalArtifactKind.Cache, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalArtifactClearReceipt(LocalArtifactKind.Cache, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LocalArtifactControlError(
                LocalArtifactControlErrorCode.PartialRemoval,
                "Partially removed.",
                -1,
                0));
    }

    [Fact]
    public void ResultRepresentsExactlyOneOutcome()
    {
        var receipt = new LocalArtifactClearReceipt(LocalArtifactKind.Cache, 2, 30);
        var success =
            LocalArtifactControlResult<LocalArtifactClearReceipt>.Success(receipt);
        var error = new LocalArtifactControlError(
            LocalArtifactControlErrorCode.PartialRemoval,
            "Local artifacts were only partially removed.",
            filesRemoved: 1,
            bytesRemoved: 10);
        var failure =
            LocalArtifactControlResult<LocalArtifactClearReceipt>.Failure(error);

        Assert.True(success.IsSuccess);
        Assert.Same(receipt, success.Value);
        Assert.Null(success.Error);
        Assert.False(failure.IsSuccess);
        Assert.Null(failure.Value);
        Assert.Same(error, failure.Error);
        Assert.Equal(1, failure.Error!.FilesRemoved);
        Assert.Equal(10, failure.Error.BytesRemoved);
    }
}
