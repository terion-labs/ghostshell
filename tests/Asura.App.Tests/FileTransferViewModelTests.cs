using Asura.App.ViewModels;
using Asura.Application;
using Asura.Core;

namespace Asura.App.Tests;

public sealed class FileTransferViewModelTests
{
    [Fact]
    public async Task Completed_transfer_is_projected_and_notified_once()
    {
        var queue = new QueueStub();
        var completed = new List<FilePanelTransferSnapshot>();
        using var viewModel = new FileTransferViewModel(
            queue,
            _ => queue,
            transfer =>
            {
                completed.Add(transfer);
                return Task.CompletedTask;
            },
            _ => { },
            new ImmediateDispatcher());
        var snapshot = Snapshot(FilePanelTransferState.Completed);

        queue.Publish(snapshot);
        queue.Publish(snapshot);
        await Task.Yield();

        var row = Assert.Single(viewModel.Transfers);
        Assert.Equal(snapshot.Id, row.Id);
        Assert.Single(completed);
        Assert.True(viewModel.HasTransfers);
        Assert.Equal("Transfers complete", viewModel.StatusText);
    }

    [Fact]
    public async Task Queue_mutations_and_errors_are_owned_by_the_view_model()
    {
        var queue = new QueueStub();
        var errors = new List<string>();
        using var viewModel = new FileTransferViewModel(
            queue,
            _ => queue,
            _ => Task.CompletedTask,
            errors.Add,
            new ImmediateDispatcher());
        var request = Request();

        Assert.True(await viewModel.EnqueueAsync(request, CancellationToken.None));
        queue.CancelError = new FilePanelError(
            FilePanelErrorCode.Conflict,
            "test.transfer.conflict",
            "Cannot cancel this transfer.",
            Retryable: false);
        Assert.False(await viewModel.CancelAsync(queue.Transfers.Single().Id, CancellationToken.None));

        Assert.Equal(1, queue.EnqueueCount);
        Assert.Contains("Cannot cancel", Assert.Single(errors), StringComparison.Ordinal);
    }

    private static FilePanelTransferSnapshot Snapshot(FilePanelTransferState state)
    {
        var request = Request();
        return new(
            FilePanelTransferId.New(),
            request,
            request.Destination,
            state,
            state.ToString(),
            BytesTransferred: 10,
            TotalBytes: 10,
            Error: null,
            DateTimeOffset.UnixEpoch,
            StartedAt: DateTimeOffset.UnixEpoch,
            CompletedAt: state == FilePanelTransferState.Completed
                ? DateTimeOffset.UnixEpoch
                : null);
    }

    private static FilePanelTransferRequest Request()
    {
        var source = new FilePanelLocation(
            "source",
            "local",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        var destination = new FilePanelLocation(
            "destination",
            "local",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        return new(
            source,
            destination,
            FilePanelTransferOperation.Copy,
            FilePanelConflictPolicy.Fail);
    }

    private sealed class QueueStub : IFileTransferQueueClient
    {
        private readonly List<FilePanelTransferSnapshot> _transfers = [];

        public IReadOnlyList<FilePanelTransferSnapshot> Transfers => _transfers;

        public FilePanelError? CancelError { get; set; }

        public int EnqueueCount { get; private set; }

        public event EventHandler? TransfersChanged;

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueAsync(
            FilePanelTransferRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnqueueCount++;
            var snapshot = Snapshot(FilePanelTransferState.Queued);
            _transfers.Add(snapshot);
            TransfersChanged?.Invoke(this, EventArgs.Empty);
            return ValueTask.FromResult(FilePanelResult<FilePanelTransferSnapshot>.Success(snapshot));
        }

        public ValueTask<FilePanelResult<Unit>> CancelAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CancelError is null
                ? FilePanelResult<Unit>.Success(Unit.Value)
                : FilePanelResult<Unit>.Failure(CancelError));

        public ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryAsync(
            FilePanelTransferId id,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(FilePanelResult<FilePanelTransferSnapshot>.Success(
                _transfers.Single(transfer => transfer.Id == id)));

        public void Publish(FilePanelTransferSnapshot snapshot)
        {
            _transfers.Clear();
            _transfers.Add(snapshot);
            TransfersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class ImmediateDispatcher : IUiThreadDispatcher
    {
        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
