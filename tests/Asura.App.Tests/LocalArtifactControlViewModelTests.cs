using Asura.App.ViewModels;
using Asura.Application;

namespace Asura.App.Tests;

public sealed class LocalArtifactControlViewModelTests
{
    [Fact]
    public async Task StartLoadsBoundedCacheAndInactiveLogMetadata()
    {
        var source = new RecordingLocalArtifactControl();
        source.InspectResults.Enqueue(Success(Inventory(
            new(LocalArtifactKind.Cache, 2, 1536),
            new(LocalArtifactKind.InactiveApplicationLogs, 1, 2 * 1024 * 1024))));
        using var viewModel = new LocalArtifactControlViewModel(source);

        viewModel.Start();
        await viewModel.Initialization;

        Assert.Equal(2, viewModel.Items.Count);
        var cache = viewModel.Items[0];
        Assert.Equal("App-managed cache", cache.Title);
        Assert.Equal("2 files", cache.FileCountLabel);
        Assert.Equal("1.5 KiB", cache.SizeLabel);
        Assert.True(cache.HasFiles);
        var logs = viewModel.Items[1];
        Assert.Equal("Inactive application logs", logs.Title);
        Assert.Equal("1 file", logs.FileCountLabel);
        Assert.Equal("2.0 MiB", logs.SizeLabel);
        Assert.True(viewModel.CanClearItems);
        Assert.False(viewModel.HasError);
        Assert.Contains("3 files", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyInventoryIsTruthfulAndCannotBeCleared()
    {
        var source = new RecordingLocalArtifactControl();
        source.InspectResults.Enqueue(Success(EmptyInventory()));
        using var viewModel = new LocalArtifactControlViewModel(source);

        viewModel.Start();
        await viewModel.Initialization;

        Assert.Equal(2, viewModel.Items.Count);
        Assert.All(viewModel.Items, item =>
        {
            Assert.False(item.HasFiles);
            Assert.Equal("No files", item.FileCountLabel);
            Assert.Equal("0 B", item.SizeLabel);
        });
        Assert.Equal(
            "No app-managed cache files or inactive persistent logs are stored.",
            viewModel.StatusMessage);
    }

    [Fact]
    public async Task ClearUsesTheSelectedKindThenRefreshesInventory()
    {
        var source = new RecordingLocalArtifactControl
        {
            ClearResult = Success(new LocalArtifactClearReceipt(
                LocalArtifactKind.Cache,
                2,
                1536)),
        };
        source.InspectResults.Enqueue(Success(Inventory(
            new(LocalArtifactKind.Cache, 2, 1536),
            new(LocalArtifactKind.InactiveApplicationLogs, 1, 256))));
        source.InspectResults.Enqueue(Success(Inventory(
            new(LocalArtifactKind.Cache, 0, 0),
            new(LocalArtifactKind.InactiveApplicationLogs, 1, 256))));
        using var viewModel = new LocalArtifactControlViewModel(source);
        viewModel.Start();
        await viewModel.Initialization;

        await viewModel.ClearAsync(viewModel.Items[0]);

        Assert.Equal(LocalArtifactKind.Cache, source.ClearedKind);
        Assert.Equal(2, source.InspectCalls);
        Assert.Equal(2, viewModel.Items.Count);
        Assert.False(viewModel.Items[0].HasFiles);
        Assert.True(viewModel.Items[1].HasFiles);
        Assert.Equal("Cleared 2 files (1.5 KiB).", viewModel.StatusMessage);
    }

    [Fact]
    public async Task SuccessfulClearFollowedByRefreshFailureDoesNotShowStaleTarget()
    {
        var source = new RecordingLocalArtifactControl
        {
            ClearResult = Success(new LocalArtifactClearReceipt(
                LocalArtifactKind.Cache,
                1,
                64)),
        };
        source.InspectResults.Enqueue(Success(Inventory(
            new(LocalArtifactKind.Cache, 1, 64),
            new(LocalArtifactKind.InactiveApplicationLogs, 1, 32))));
        source.InspectResults.Enqueue(Failure<LocalArtifactInventory>(
            LocalArtifactControlErrorCode.Unavailable));
        using var viewModel = new LocalArtifactControlViewModel(source);
        viewModel.Start();
        await viewModel.Initialization;

        await viewModel.ClearAsync(viewModel.Items[0]);

        Assert.True(viewModel.HasError);
        var remaining = Assert.Single(viewModel.Items);
        Assert.Equal("Inactive application logs", remaining.Title);
        Assert.StartsWith("Cleared 1 file (64 B).", viewModel.StatusMessage);
        Assert.Contains(
            nameof(LocalArtifactControlErrorCode.Unavailable),
            viewModel.StatusMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartialRemovalReportsReceiptAndHidesStaleTarget()
    {
        var source = new RecordingLocalArtifactControl
        {
            ClearResult = LocalArtifactControlResult<LocalArtifactClearReceipt>.Failure(
                new LocalArtifactControlError(
                    LocalArtifactControlErrorCode.PartialRemoval,
                    "private-path-canary",
                    filesRemoved: 3,
                    bytesRemoved: 42)),
        };
        source.InspectResults.Enqueue(Success(Inventory(
            new(LocalArtifactKind.Cache, 5, 99),
            new(LocalArtifactKind.InactiveApplicationLogs, 1, 10))));
        using var viewModel = new LocalArtifactControlViewModel(source);
        viewModel.Start();
        await viewModel.Initialization;

        await viewModel.ClearAsync(viewModel.Items[0]);

        Assert.True(viewModel.HasError);
        var remaining = Assert.Single(viewModel.Items);
        Assert.Equal("Inactive application logs", remaining.Title);
        Assert.Contains("Removed 3 files", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path-canary", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InventoryFailureFailsClosedWithoutLeakingDetails()
    {
        var source = new RecordingLocalArtifactControl();
        source.InspectResults.Enqueue(
            LocalArtifactControlResult<LocalArtifactInventory>.Failure(
                new LocalArtifactControlError(
                    LocalArtifactControlErrorCode.UnsafeLayout,
                    "private-path-canary")));
        using var viewModel = new LocalArtifactControlViewModel(source);

        viewModel.Start();
        await viewModel.Initialization;

        Assert.True(viewModel.HasError);
        Assert.Empty(viewModel.Items);
        Assert.False(viewModel.CanClearItems);
        Assert.Contains(
            nameof(LocalArtifactControlErrorCode.UnsafeLayout),
            viewModel.StatusMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("private-path-canary", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    private static LocalArtifactInventory EmptyInventory() => Inventory(
        new(LocalArtifactKind.Cache, 0, 0),
        new(LocalArtifactKind.InactiveApplicationLogs, 0, 0));

    private static LocalArtifactInventory Inventory(params LocalArtifactSummary[] summaries) =>
        new(summaries);

    private static LocalArtifactControlResult<T> Success<T>(T value) =>
        LocalArtifactControlResult<T>.Success(value);

    private static LocalArtifactControlResult<T> Failure<T>(
        LocalArtifactControlErrorCode code) =>
        LocalArtifactControlResult<T>.Failure(new LocalArtifactControlError(
            code,
            "Configured failure."));

    private sealed class RecordingLocalArtifactControl : ILocalArtifactControl
    {
        public Queue<LocalArtifactControlResult<LocalArtifactInventory>> InspectResults { get; } = [];

        public LocalArtifactControlResult<LocalArtifactClearReceipt> ClearResult { get; set; } =
            Success(new LocalArtifactClearReceipt(LocalArtifactKind.Cache, 0, 0));

        public int InspectCalls { get; private set; }

        public LocalArtifactKind? ClearedKind { get; private set; }

        public ValueTask<LocalArtifactControlResult<LocalArtifactInventory>> InspectAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCalls++;
            return ValueTask.FromResult(InspectResults.Dequeue());
        }

        public ValueTask<LocalArtifactControlResult<LocalArtifactClearReceipt>> ClearAsync(
            LocalArtifactKind kind,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearedKind = kind;
            return ValueTask.FromResult(ClearResult);
        }
    }
}
