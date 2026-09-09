using System.Globalization;
using Asura.Application;

namespace Asura.App.ViewModels;

public sealed record LocalArtifactItemViewModel
{
    internal LocalArtifactItemViewModel(LocalArtifactSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        Kind = summary.Kind;
        FileCount = summary.FileCount;
        TotalBytes = summary.TotalBytes;
        (Title, Description, ClearAutomationName) = summary.Kind switch
        {
            LocalArtifactKind.Cache => (
                "App-managed cache",
                "Temporary files stored in Asura’s dedicated cache location.",
                "Clear Asura app-managed cache"),
            LocalArtifactKind.InactiveApplicationLogs => (
                "Inactive application logs",
                "Persistent logs from earlier runs. This build does not write a persistent active log.",
                "Clear inactive Asura application logs"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(summary),
                summary.Kind,
                "The local artifact kind is not supported."),
        };
        FileCountLabel = summary.FileCount == 0
            ? "No files"
            : $"{summary.FileCount.ToString("N0", CultureInfo.InvariantCulture)} "
                + $"file{(summary.FileCount == 1 ? string.Empty : "s")}";
        SizeLabel = FormatBytes(summary.TotalBytes);
    }

    internal LocalArtifactKind Kind { get; }

    public string Title { get; }

    public string Description { get; }

    public string ClearAutomationName { get; }

    public long FileCount { get; }

    public long TotalBytes { get; }

    public string FileCountLabel { get; }

    public string SizeLabel { get; }

    public bool HasFiles => FileCount > 0;

    internal static string FormatBytes(long bytes)
    {
        const long kibibyte = 1024;
        const long mebibyte = kibibyte * 1024;
        const long gibibyte = mebibyte * 1024;
        return bytes switch
        {
            < kibibyte => $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} B",
            < mebibyte =>
                $"{(bytes / (double)kibibyte).ToString("0.0", CultureInfo.InvariantCulture)} KiB",
            < gibibyte =>
                $"{(bytes / (double)mebibyte).ToString("0.0", CultureInfo.InvariantCulture)} MiB",
            _ => $"{(bytes / (double)gibibyte).ToString("0.0", CultureInfo.InvariantCulture)} GiB",
        };
    }
}

/// <summary>
/// Presents bounded metadata and clear actions for Asura-owned cache and inactive log files.
/// Host caches, debug trace sinks, durable definitions, recovery data, and active logs are outside
/// this control's boundary.
/// </summary>
public sealed class LocalArtifactControlViewModel : ObservableObject, IDisposable
{
    private readonly ILocalArtifactControl _control;
    private readonly IUiThreadDispatcher _uiThreadDispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _startGate = new();
    private Task _initialization = Task.CompletedTask;
    private IReadOnlyList<LocalArtifactItemViewModel> _items = [];
    private bool _started;
    private bool _isBusy;
    private bool _hasError;
    private string _statusMessage = "Loading app-managed cache and log metadata…";
    private bool _disposed;

    public LocalArtifactControlViewModel(
        ILocalArtifactControl control,
        IUiThreadDispatcher? uiThreadDispatcher = null)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
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

    public IReadOnlyList<LocalArtifactItemViewModel> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(HasInventory));
                OnPropertyChanged(nameof(HasNoInventory));
            }
        }
    }

    public bool HasInventory => Items.Count > 0;

    public bool HasNoInventory => !HasInventory && !IsBusy && !HasError;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(HasNoInventory));
                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(CanClearItems));
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
                OnPropertyChanged(nameof(HasNoInventory));
                OnPropertyChanged(nameof(CanClearItems));
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

    public string StatusAutomationName => $"App-managed storage status: {StatusMessage}";

    public bool CanRefresh => !IsBusy;

    public bool CanClearItems => !IsBusy && !HasError;

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
                    StatusMessage = "Loading app-managed cache and log metadata…";
                }, token).ConfigureAwait(false);
                var result = await _control.InspectAsync(token).ConfigureAwait(false);
                await PublishAsync(() => ApplyInventoryResult(result), token)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    public Task ClearAsync(
        LocalArtifactItemViewModel item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunExclusiveAsync(
            token => ClearCoreAsync(item.Kind, item.Title, token),
            cancellationToken);
    }

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

    private async Task ClearCoreAsync(
        LocalArtifactKind kind,
        string title,
        CancellationToken cancellationToken)
    {
        await PublishAsync(() =>
        {
            IsBusy = true;
            HasError = false;
            StatusMessage = $"Clearing {title.ToLowerInvariant()}…";
        }, cancellationToken).ConfigureAwait(false);
        var cleared = await _control.ClearAsync(kind, cancellationToken).ConfigureAwait(false);
        if (!cleared.IsSuccess)
        {
            await PublishAsync(
                () => ApplyClearFailure(kind, cleared.Error!),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var receipt = cleared.Value!;
        var clearStatus = ClearStatus(receipt);
        await PublishAsync(() =>
        {
            RemoveStaleKind(kind);
            StatusMessage = $"{clearStatus} Loading current app-managed storage metadata…";
        }, CancellationToken.None).ConfigureAwait(false);

        LocalArtifactControlResult<LocalArtifactInventory> inventory;
        try
        {
            inventory = await _control.InspectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PublishAsync(
                () => ApplyPostClearRefreshFailure(
                    clearStatus,
                    LocalArtifactControlErrorCode.Cancelled),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await PublishAsync(
                () => ApplyPostClearRefreshFailure(
                    clearStatus,
                    LocalArtifactControlErrorCode.IoFailure),
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await PublishAsync(() =>
        {
            if (!inventory.IsSuccess)
            {
                ApplyPostClearRefreshFailure(clearStatus, inventory.Error!.Code);
                return;
            }

            ApplyInventory(inventory.Value!);
            StatusMessage = clearStatus;
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
            await TryPublishAsync(() =>
            {
                HasError = false;
                StatusMessage = "The app-managed storage operation was cancelled.";
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await TryPublishAsync(() =>
            {
                HasError = true;
                StatusMessage =
                    "App-managed cache and log metadata is unavailable. No further files were removed.";
            }).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await TryPublishAsync(() => IsBusy = false).ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }
    }

    private void ApplyInventoryResult(
        LocalArtifactControlResult<LocalArtifactInventory> result)
    {
        if (!result.IsSuccess)
        {
            HasError = result.Error!.Code != LocalArtifactControlErrorCode.Cancelled;
            Items = [];
            StatusMessage = result.Error.Code == LocalArtifactControlErrorCode.Cancelled
                ? "The app-managed storage operation was cancelled."
                : $"App-managed cache and log metadata could not be loaded. ({result.Error.Code})";
            return;
        }

        ApplyInventory(result.Value!);
    }

    private void ApplyInventory(LocalArtifactInventory inventory)
    {
        HasError = false;
        Items = [.. inventory.Artifacts
            .OrderBy(static item => item.Kind)
            .Select(static item => new LocalArtifactItemViewModel(item))];
        StatusMessage = InventoryStatus(inventory);
    }

    private void ApplyClearFailure(
        LocalArtifactKind kind,
        LocalArtifactControlError error)
    {
        if (error.Code == LocalArtifactControlErrorCode.PartialRemoval)
        {
            RemoveStaleKind(kind);
            HasError = true;
            StatusMessage =
                $"Removed {FormatFileCount(error.FilesRemoved)}, but cleanup did not finish. "
                + $"Refresh before trying again. ({error.Code})";
            return;
        }

        HasError = error.Code != LocalArtifactControlErrorCode.Cancelled;
        StatusMessage = error.Code == LocalArtifactControlErrorCode.Cancelled
            ? "The app-managed storage operation was cancelled before any files were removed."
            : $"The selected app-managed files could not be cleared. ({error.Code})";
    }

    private void ApplyPostClearRefreshFailure(
        string clearStatus,
        LocalArtifactControlErrorCode errorCode)
    {
        HasError = true;
        StatusMessage =
            $"{clearStatus} Current app-managed storage metadata could not be loaded. "
            + $"({errorCode})";
    }

    private void RemoveStaleKind(LocalArtifactKind kind)
    {
        Items = [.. Items.Where(item => item.Kind != kind)];
    }

    private static string InventoryStatus(LocalArtifactInventory inventory)
    {
        var files = inventory.Artifacts.Sum(static item => item.FileCount);
        var bytes = inventory.Artifacts.Sum(static item => item.TotalBytes);
        return files == 0
            ? "No app-managed cache files or inactive persistent logs are stored."
            : $"{FormatFileCount(files)} use {LocalArtifactItemViewModel.FormatBytes(bytes)}.";
    }

    private static string ClearStatus(LocalArtifactClearReceipt receipt) =>
        receipt.FilesRemoved == 0
            ? "The selected app-managed storage was already empty."
            : $"Cleared {FormatFileCount(receipt.FilesRemoved)} "
                + $"({LocalArtifactItemViewModel.FormatBytes(receipt.BytesRemoved)}).";

    private static string FormatFileCount(long count) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} "
        + $"file{(count == 1 ? string.Empty : "s")}";

    private Task PublishAsync(Action action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _uiThreadDispatcher.InvokeAsync(action, cancellationToken);
    }

    private async Task TryPublishAsync(Action action)
    {
        try
        {
            await PublishAsync(action, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
