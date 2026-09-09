using Asura.Application.ApplicationUpdates;

namespace Asura.App.ViewModels;

public sealed class ApplicationUpdateViewModel : ObservableObject, IDisposable
{
    private readonly IApplicationUpdateService _updates;
    private readonly IUiThreadDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private ApplicationUpdateSnapshot _snapshot;
    private bool _disposed;

    public ApplicationUpdateViewModel(
        IApplicationUpdateService updates,
        IUiThreadDispatcher dispatcher)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _snapshot = updates.Snapshot;
        _updates.Changed += OnUpdateChanged;
    }

    public string Channel => _snapshot.Distribution.Source switch
    {
        DistributionSource.GitHubRelease =>
            $"Direct · GitHub · {_snapshot.Distribution.Channel}",
        DistributionSource.AppleAppStore => "Apple App Store",
        DistributionSource.MicrosoftStore => "Microsoft Store",
        DistributionSource.LinuxPackageManager => "Linux package manager",
        _ => "Development build",
    };

    public string Status => _snapshot.Stage switch
    {
        ApplicationUpdateStage.Unavailable when
            _snapshot.Error == ApplicationUpdateError.NotInstalledByVelopack =>
            "This direct build was not installed by Velopack, so it cannot update in place.",
        ApplicationUpdateStage.Unavailable =>
            "Updates are unavailable for this build.",
        ApplicationUpdateStage.ManagedExternally =>
            "Your install source checks, downloads, and applies updates.",
        ApplicationUpdateStage.Idle =>
            "No check has run yet. Asura checks only when you ask.",
        ApplicationUpdateStage.Checking => "Checking for updates…",
        ApplicationUpdateStage.UpToDate => "Asura is up to date.",
        ApplicationUpdateStage.Available when !_snapshot.ApplyAllowed =>
            $"Version {_snapshot.AvailableVersion} is available, but this system-wide install requires the signed installer.",
        ApplicationUpdateStage.Available =>
            $"Version {_snapshot.AvailableVersion} is available.",
        ApplicationUpdateStage.Downloading =>
            $"Downloading version {_snapshot.AvailableVersion} · {_snapshot.DownloadProgress ?? 0}%",
        ApplicationUpdateStage.ReadyToRestart when !_snapshot.ApplyAllowed =>
            $"Version {_snapshot.AvailableVersion} is downloaded, but this system-wide install requires the signed installer.",
        ApplicationUpdateStage.ReadyToRestart =>
            $"Version {_snapshot.AvailableVersion} is ready. Restart to apply it.",
        ApplicationUpdateStage.Failed => FailureStatus(_snapshot.Error),
        _ => "Updates are unavailable for this build.",
    };

    public bool CanCheck => _snapshot.CanCheck;

    public bool CanDownload => _snapshot.CanDownload;

    public bool CanRestartToApply => _snapshot.CanRestartToApply;

    public bool IsDownloading =>
        _snapshot.Stage == ApplicationUpdateStage.Downloading;

    public int DownloadProgress => _snapshot.DownloadProgress ?? 0;

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _updates.CheckAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _updates.DownloadAsync(cancellationToken).ConfigureAwait(false);
    }

    public void RestartToApply()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _updates.RestartToApply();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updates.Changed -= OnUpdateChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async void OnUpdateChanged(
        object? sender,
        ApplicationUpdateSnapshot snapshot)
    {
        _ = sender;
        try
        {
            await _dispatcher.InvokeAsync(
                () => Apply(snapshot),
                _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void Apply(ApplicationUpdateSnapshot snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(nameof(Channel));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanRestartToApply));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(DownloadProgress));
    }

    private static string FailureStatus(ApplicationUpdateError error) => error switch
    {
        ApplicationUpdateError.CheckFailed =>
            "The update check failed. Check your connection and try again.",
        ApplicationUpdateError.DownloadFailed =>
            "The update download failed. Try the download again.",
        ApplicationUpdateError.ApplyFailed =>
            "Asura could not start the updater. The downloaded update was not applied.",
        _ => "The update operation failed.",
    };
}
