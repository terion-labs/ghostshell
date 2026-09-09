using System.Collections.ObjectModel;
using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Owns file-transfer queue mutations, live subscription, and stable row
/// projection. Runtime panel refresh after completion is supplied as a shell
/// composition callback.
/// </summary>
public sealed class FileTransferViewModel : ObservableObject, IDisposable
{
    private readonly IFileTransferQueueClient _queue;
    private readonly Func<FilePanelTransferId, IFileTransferQueueClient> _resolveQueue;
    private readonly Func<FilePanelTransferSnapshot, Task> _transferCompleted;
    private readonly Action<string> _setError;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly HashSet<FilePanelTransferId> _completedTransfers = [];
    private bool _disposed;

    public FileTransferViewModel(
        IFileTransferQueueClient queue,
        Func<FilePanelTransferId, IFileTransferQueueClient> resolveQueue,
        Func<FilePanelTransferSnapshot, Task> transferCompleted,
        Action<string> setError,
        IUiThreadDispatcher dispatcher)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _resolveQueue = resolveQueue ?? throw new ArgumentNullException(nameof(resolveQueue));
        _transferCompleted = transferCompleted
            ?? throw new ArgumentNullException(nameof(transferCompleted));
        _setError = setError ?? throw new ArgumentNullException(nameof(setError));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _queue.TransfersChanged += OnTransfersChanged;
        Refresh();
    }

    public ObservableCollection<FileTransferItemViewModel> Transfers { get; } = [];

    public bool HasTransfers => Transfers.Count > 0;

    public bool HasNoTransfers => !HasTransfers;

    public int ActiveCount => Transfers.Count(transfer => transfer.IsActive);

    public int FailedCount => Transfers.Count(transfer => transfer.HasError);

    public string StatusText
    {
        get
        {
            var active = Transfers.FirstOrDefault(transfer => transfer.IsActive);
            if (active is not null)
            {
                return ActiveCount == 1
                    ? active.HasKnownProgress
                        ? $"Transfer · {active.ProgressPercent:0}%"
                        : "Transfer in progress"
                    : $"{ActiveCount} transfers";
            }

            if (FailedCount > 0)
            {
                return FailedCount == 1
                    ? "1 transfer failed"
                    : $"{FailedCount} transfers failed";
            }

            return "Transfers complete";
        }
    }

    public async ValueTask<bool> CancelAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var result = await _resolveQueue(id).CancelAsync(id, cancellationToken);
        return CompleteMutation(result);
    }

    public async ValueTask<bool> EnqueueAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var result = await _queue.EnqueueAsync(request, cancellationToken);
        return CompleteMutation(result);
    }

    public async ValueTask<bool> RetryAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var result = await _resolveQueue(id).RetryAsync(id, cancellationToken);
        return CompleteMutation(result);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.TransfersChanged -= OnTransfersChanged;
    }

    private bool CompleteMutation<T>(FilePanelResult<T> result)
    {
        if (!result.IsSuccess)
        {
            _setError(result.Error!.Message);
            return false;
        }

        Refresh();
        return true;
    }

    private void OnTransfersChanged(object? sender, EventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        _ = RefreshOnUiThreadAsync();
    }

    private async Task RefreshOnUiThreadAsync()
    {
        try
        {
            await _dispatcher.InvokeAsync(Refresh, CancellationToken.None);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
    }

    private void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        var snapshots = _queue.Transfers;
        var rows = snapshots.Select(snapshot =>
            new FileTransferItemViewModel(
                snapshot.Id,
                FileLocationPresentation.Display(snapshot.Request.Source),
                FileLocationPresentation.Display(snapshot.EffectiveDestination),
                snapshot.Request.Operation.ToString(),
                snapshot.State.ToString(),
                snapshot.Stage,
                FormatProgress(snapshot),
                snapshot.Error?.Message,
                snapshot.Error is not null,
                snapshot.CanCancel,
                snapshot.CanRetry,
                snapshot.State is
                    FilePanelTransferState.Queued or FilePanelTransferState.Running,
                snapshot.TotalBytes is > 0,
                Percent(snapshot),
                snapshot.QueuedAt))
            .ToArray();
        Synchronize(rows);
        OnPropertyChanged(nameof(HasTransfers));
        OnPropertyChanged(nameof(HasNoTransfers));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(StatusText));

        foreach (var snapshot in snapshots.Where(snapshot =>
                     snapshot.State == FilePanelTransferState.Completed
                     && _completedTransfers.Add(snapshot.Id)))
        {
            _ = _transferCompleted(snapshot);
        }
    }

    private void Synchronize(IReadOnlyList<FileTransferItemViewModel> latest)
    {
        var existingById = Transfers.ToDictionary(transfer => transfer.Id);
        for (var index = 0; index < latest.Count; index++)
        {
            var candidate = latest[index];
            if (!existingById.TryGetValue(candidate.Id, out var existing))
            {
                Transfers.Insert(index, candidate);
                continue;
            }

            existing.UpdateFrom(candidate);
            var currentIndex = Transfers.IndexOf(existing);
            if (currentIndex != index)
            {
                Transfers.Move(currentIndex, index);
            }
        }

        var liveIds = latest.Select(transfer => transfer.Id).ToHashSet();
        for (var index = Transfers.Count - 1; index >= 0; index--)
        {
            if (!liveIds.Contains(Transfers[index].Id))
            {
                Transfers.RemoveAt(index);
            }
        }
    }

    private static string FormatProgress(FilePanelTransferSnapshot snapshot)
    {
        if (snapshot.TotalBytes is > 0 and var total)
        {
            var percent = Percent(snapshot);
            return $"{percent.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}% · {snapshot.BytesTransferred:N0} / {total:N0} bytes";
        }

        return $"{snapshot.BytesTransferred:N0} bytes";
    }

    private static double Percent(FilePanelTransferSnapshot snapshot) =>
        snapshot.TotalBytes is > 0 and var total
            ? Math.Clamp((double)snapshot.BytesTransferred / total * 100, 0, 100)
            : 0;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
