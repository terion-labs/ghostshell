using System.Globalization;
using Asura.Application;

namespace Asura.App.ViewModels;

public sealed record RecoveryRunItemViewModel
{
    internal RecoveryRunItemViewModel(
        string runId,
        int displayIndex,
        long snapshotCount,
        long payloadBytes,
        DateTimeOffset lastUpdatedAt)
    {
        var displayNumber = displayIndex.ToString(CultureInfo.InvariantCulture);
        RunId = runId;
        Title = $"Saved recovery {displayNumber}";
        ClearAutomationName = $"Clear saved recovery {displayNumber}";
        SnapshotLabel =
            $"{snapshotCount.ToString("N0", CultureInfo.InvariantCulture)} "
            + $"snapshot{(snapshotCount == 1 ? string.Empty : "s")}";
        SizeLabel = FormatBytes(payloadBytes);
        UpdatedLabel =
            $"Last saved {lastUpdatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}";
    }

    internal string RunId { get; }

    public string Title { get; }

    public string ClearAutomationName { get; }

    public string SnapshotLabel { get; }

    public string SizeLabel { get; }

    public string UpdatedLabel { get; }

    private static string FormatBytes(long bytes)
    {
        const long kibibyte = 1024;
        const long mebibyte = kibibyte * 1024;
        return bytes switch
        {
            < kibibyte => $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} B",
            < mebibyte =>
                $"{(bytes / (double)kibibyte).ToString("0.0", CultureInfo.InvariantCulture)} KiB",
            _ => $"{(bytes / (double)mebibyte).ToString("0.0", CultureInfo.InvariantCulture)} MiB",
        };
    }
}

/// <summary>
/// Presents metadata-only controls for inactive runtime recovery snapshots.
/// Payloads are never requested, parsed, or exposed by this view model.
/// </summary>
public sealed class RecoveryDataControlViewModel : ObservableObject, IDisposable
{
    private readonly IRuntimeRecoveryDataControl _dataControl;
    private readonly IUiThreadDispatcher _uiThreadDispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _startGate = new();
    private Task _initialization = Task.CompletedTask;
    private IReadOnlyList<RecoveryRunItemViewModel> _runs = [];
    private bool _started;
    private bool _isBusy;
    private bool _hasError;
    private string _statusMessage = "Loading saved recovery metadata…";
    private int _listedRunCount;
    private long _listedSnapshotCount;
    private long _listedPayloadBytes;
    private bool _hasAdditionalRuns;
    private bool _disposed;

    public RecoveryDataControlViewModel(
        IRuntimeRecoveryDataControl dataControl,
        IUiThreadDispatcher? uiThreadDispatcher = null)
    {
        _dataControl = dataControl ?? throw new ArgumentNullException(nameof(dataControl));
        _uiThreadDispatcher = uiThreadDispatcher ?? AvaloniaUiThreadDispatcher.Instance;
    }

    public Task Initialization
    {
        get
        {
            lock (_startGate)
            {
                return _initialization;
            }
        }
    }

    public IReadOnlyList<RecoveryRunItemViewModel> Runs
    {
        get => _runs;
        private set
        {
            if (SetProperty(ref _runs, value))
            {
                OnPropertyChanged(nameof(HasRuns));
                OnPropertyChanged(nameof(HasNoRuns));
            }
        }
    }

    public bool HasRuns => Runs.Count > 0;

    public bool HasNoRuns => !HasRuns && !IsBusy && !HasError;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(HasNoRuns));
                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(CanClearAll));
                OnPropertyChanged(nameof(CanClearRuns));
            }
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (SetProperty(ref _hasError, value))
            {
                OnPropertyChanged(nameof(HasNoRuns));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(StatusAutomationName));
            }
        }
    }

    public string StatusAutomationName => $"Crash recovery data status: {StatusMessage}";

    public int ListedRunCount => _listedRunCount;

    public long ListedSnapshotCount => _listedSnapshotCount;

    public long ListedPayloadBytes => _listedPayloadBytes;

    public bool HasAdditionalRuns => _hasAdditionalRuns;

    public bool CanRefresh => !IsBusy;

    public bool CanClearAll => HasRuns && !IsBusy && !HasError;

    public bool CanClearRuns => !IsBusy && !HasError;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_startGate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _initialization = RefreshAsync(_lifetime.Token);
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            async token =>
            {
                await PublishAsync(() =>
                {
                    IsBusy = true;
                    HasError = false;
                    StatusMessage = "Loading saved recovery metadata…";
                }, token).ConfigureAwait(false);
                var result = await _dataControl.ListAsync(token).ConfigureAwait(false);
                await PublishAsync(
                    () => ApplyInventoryResult(result),
                    token).ConfigureAwait(false);
            },
            cancellationToken);

    public Task DiscardRunAsync(
        RecoveryRunItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunExclusiveAsync(
            token => DiscardAsync(
                () => _dataControl.DiscardRunAsync(item.RunId, token),
                "saved recovery",
                token),
            cancellationToken);
    }

    public Task DiscardAllAsync(CancellationToken cancellationToken = default) =>
        RunExclusiveAsync(
            token => DiscardAsync(
                () => _dataControl.DiscardAllAsync(token),
                "saved recovery data",
                token),
            cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task DiscardAsync(
        Func<ValueTask<ApplicationRunResult<long>>> discard,
        string subject,
        CancellationToken cancellationToken)
    {
        await PublishAsync(() =>
        {
            IsBusy = true;
            HasError = false;
            StatusMessage = $"Clearing {subject}…";
        }, cancellationToken).ConfigureAwait(false);
        var discarded = await discard().ConfigureAwait(false);
        if (!discarded.IsSuccess)
        {
            await PublishAsync(
                () => ApplyFailure(discarded.Error!, $"The {subject} could not be cleared."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var discardStatus = DiscardStatus(discarded.Value, subject);
        await PublishAsync(() =>
        {
            ClearInventory();
            StatusMessage = $"{discardStatus} Loading the remaining recovery metadata…";
        }, CancellationToken.None).ConfigureAwait(false);

        ApplicationRunResult<RuntimeRecoveryInventory> inventory;
        try
        {
            inventory = await _dataControl.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PublishAsync(
                () => ApplyPostDiscardRefreshFailure(
                    discardStatus,
                    ApplicationRunErrorCode.Cancelled),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await PublishAsync(
                () => ApplyPostDiscardRefreshFailure(
                    discardStatus,
                    ApplicationRunErrorCode.StorageFailure),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await PublishAsync(() =>
        {
            if (!inventory.IsSuccess)
            {
                ApplyPostDiscardRefreshFailure(discardStatus, inventory.Error!.Code);
                return;
            }

            ApplyInventory(inventory.Value!);
            StatusMessage = discardStatus;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await operation(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            try
            {
                await PublishAsync(() =>
                {
                    HasError = false;
                    StatusMessage = "The recovery data operation was cancelled.";
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            try
            {
                await PublishAsync(() =>
                {
                    HasError = true;
                    StatusMessage =
                        "Crash recovery metadata is unavailable. No recovery data was changed.";
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            try
            {
                await PublishAsync(() => IsBusy = false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    private void ApplyInventoryResult(
        ApplicationRunResult<RuntimeRecoveryInventory> result)
    {
        if (!result.IsSuccess)
        {
            ApplyFailure(
                result.Error!,
                "Crash recovery metadata could not be loaded.");
            return;
        }

        ApplyInventory(result.Value!);
    }

    private void ApplyInventory(RuntimeRecoveryInventory inventory)
    {
        HasError = false;
        _listedRunCount = inventory.ListedRunCount;
        _listedSnapshotCount = inventory.ListedSnapshotCount;
        _listedPayloadBytes = inventory.ListedPayloadBytes;
        _hasAdditionalRuns = inventory.HasAdditionalRuns;
        Runs = [.. inventory.Runs
            .Select((item, index) => new RecoveryRunItemViewModel(
                item.RunId,
                index + 1,
                item.SnapshotCount,
                item.PayloadBytes,
                item.LastUpdatedAt))];
        OnPropertyChanged(nameof(ListedRunCount));
        OnPropertyChanged(nameof(ListedSnapshotCount));
        OnPropertyChanged(nameof(ListedPayloadBytes));
        OnPropertyChanged(nameof(HasAdditionalRuns));
        OnPropertyChanged(nameof(CanClearAll));
        StatusMessage = InventoryStatus(inventory);
    }

    private void ClearInventory()
    {
        _listedRunCount = 0;
        _listedSnapshotCount = 0;
        _listedPayloadBytes = 0;
        _hasAdditionalRuns = false;
        Runs = [];
        OnPropertyChanged(nameof(ListedRunCount));
        OnPropertyChanged(nameof(ListedSnapshotCount));
        OnPropertyChanged(nameof(ListedPayloadBytes));
        OnPropertyChanged(nameof(HasAdditionalRuns));
        OnPropertyChanged(nameof(CanClearAll));
    }

    private void ApplyPostDiscardRefreshFailure(
        string discardStatus,
        ApplicationRunErrorCode errorCode)
    {
        HasError = true;
        StatusMessage =
            $"{discardStatus} The remaining recovery metadata could not be loaded. "
            + $"({errorCode})";
    }

    private void ApplyFailure(ApplicationRunError error, string fallback)
    {
        HasError = error.Code != ApplicationRunErrorCode.Cancelled;
        StatusMessage = error.Code == ApplicationRunErrorCode.Cancelled
            ? "The recovery data operation was cancelled."
            : $"{fallback} ({error.Code})";
    }

    private static string DiscardStatus(long deleted, string subject) =>
        deleted == 0
            ? $"The {subject} had already been removed."
            : $"Cleared {deleted.ToString("N0", CultureInfo.InvariantCulture)} "
                + $"snapshot{(deleted == 1 ? string.Empty : "s")}.";

    private static string InventoryStatus(RuntimeRecoveryInventory inventory)
    {
        if (inventory.ListedRunCount == 0)
        {
            return "No saved crash recovery data from previous runs.";
        }

        var snapshots =
            $"{inventory.ListedSnapshotCount.ToString("N0", CultureInfo.InvariantCulture)} "
            + $"snapshot{(inventory.ListedSnapshotCount == 1 ? string.Empty : "s")}";
        var runs =
            $"{inventory.ListedRunCount.ToString("N0", CultureInfo.InvariantCulture)} "
            + $"previous run{(inventory.ListedRunCount == 1 ? string.Empty : "s")}";
        return inventory.HasAdditionalRuns
            ? $"Showing {snapshots} from the newest {runs}. "
                + "Older recovery runs are also stored."
            : $"{snapshots} from {runs}.";
    }

    private Task PublishAsync(Action action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _uiThreadDispatcher.InvokeAsync(action, cancellationToken);
    }
}
