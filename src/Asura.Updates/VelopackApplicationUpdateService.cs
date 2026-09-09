using Asura.Application.ApplicationUpdates;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Asura.Updates;

internal sealed class VelopackApplicationUpdateService : IApplicationUpdateService
{
    private const string RepositoryUrl =
        "https://github.com/terion-labs/asura";

    private readonly Action _requestShutdown;
    private readonly UpdateManager _updates;
    private UpdateInfo? _availableUpdate;
    private int _operationInProgress;

    public VelopackApplicationUpdateService(
        DistributionIdentity distribution,
        Action requestShutdown,
        IVelopackLocator? locator = null)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        ArgumentNullException.ThrowIfNull(requestShutdown);
        if (distribution.UpdateStrategy != ApplicationUpdateStrategy.Velopack)
        {
            throw new ArgumentException(
                "The distribution does not use Velopack.",
                nameof(distribution));
        }

        _requestShutdown = requestShutdown;
        _updates = new UpdateManager(
            new GithubSource(RepositoryUrl, accessToken: null, prerelease: false),
            new UpdateOptions
            {
                ExplicitChannel = distribution.Channel,
                AllowVersionDowngrade = false,
            },
            locator);
        var applyAllowed = ApplyDoesNotRequireMacOsElevation(
            Environment.ProcessPath);
        Snapshot = !_updates.IsInstalled
            ? new(
                distribution,
                ApplicationUpdateStage.Unavailable,
                Error: ApplicationUpdateError.NotInstalledByVelopack,
                ApplyAllowed: applyAllowed)
            : _updates.UpdatePendingRestart is { } pending
                ? new(
                    distribution,
                    ApplicationUpdateStage.ReadyToRestart,
                    pending.Version.ToString(),
                    ApplyAllowed: applyAllowed)
                : new(
                    distribution,
                    ApplicationUpdateStage.Idle,
                    ApplyAllowed: applyAllowed);
    }

    public event EventHandler<ApplicationUpdateSnapshot>? Changed;

    public ApplicationUpdateSnapshot Snapshot { get; private set; }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Snapshot.CanCheck)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!Snapshot.CanCheck)
            {
                return;
            }

            SetSnapshot(Snapshot with
            {
                Stage = ApplicationUpdateStage.Checking,
                AvailableVersion = null,
                DownloadProgress = null,
                Error = ApplicationUpdateError.None,
            });

            // Velopack does not expose cancellation for feed checks. Once the
            // request starts, this boundary waits for it instead of allowing a
            // second operation to race the first one.
            _availableUpdate = await _updates.CheckForUpdatesAsync()
                .ConfigureAwait(false);
            SetSnapshot(_availableUpdate is null
                ? Snapshot with { Stage = ApplicationUpdateStage.UpToDate }
                : Snapshot with
                {
                    Stage = ApplicationUpdateStage.Available,
                    AvailableVersion =
                        _availableUpdate.TargetFullRelease.Version.ToString(),
                });
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            SetFailure(ApplicationUpdateError.CheckFailed);
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
        }
    }

    public async Task DownloadAsync(CancellationToken cancellationToken)
    {
        if (_availableUpdate is null || !Snapshot.CanDownload)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _operationInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (_availableUpdate is null || !Snapshot.CanDownload)
            {
                return;
            }

            SetSnapshot(Snapshot with
            {
                Stage = ApplicationUpdateStage.Downloading,
                DownloadProgress = 0,
                Error = ApplicationUpdateError.None,
            });
            await _updates.DownloadUpdatesAsync(
                    _availableUpdate,
                    progress => SetSnapshot(Snapshot with
                    {
                        DownloadProgress = progress,
                    }),
                    cancellationToken)
                .ConfigureAwait(false);
            SetSnapshot(Snapshot with
            {
                Stage = ApplicationUpdateStage.ReadyToRestart,
                DownloadProgress = 100,
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetSnapshot(Snapshot with
            {
                Stage = ApplicationUpdateStage.Available,
                DownloadProgress = null,
            });
            throw;
        }
        catch (Exception)
        {
            SetFailure(ApplicationUpdateError.DownloadFailed);
        }
        finally
        {
            Volatile.Write(ref _operationInProgress, 0);
        }
    }

    public void RestartToApply()
    {
        var release = _availableUpdate?.TargetFullRelease
            ?? _updates.UpdatePendingRestart;
        if (release is null || !Snapshot.CanRestartToApply)
        {
            return;
        }

        try
        {
            // The external updater waits while Asura follows its normal
            // shutdown path and flushes recovery state, browser profiles, and
            // session history. It gives up after Velopack's 60-second limit.
            _updates.WaitExitThenApplyUpdates(
                release,
                // Velopack 1.2.0 refuses its macOS elevation path in silent mode.
                // Location checks are only UI guidance, never an authority boundary.
                silent: OperatingSystem.IsMacOS(),
                restart: true,
                restartArgs: null);
            _requestShutdown();
        }
        catch (Exception)
        {
            SetFailure(ApplicationUpdateError.ApplyFailed);
        }
    }

    private void SetFailure(ApplicationUpdateError error) =>
        SetSnapshot(Snapshot with
        {
            Stage = ApplicationUpdateStage.Failed,
            DownloadProgress = null,
            Error = error,
        });

    private void SetSnapshot(ApplicationUpdateSnapshot snapshot)
    {
        Snapshot = snapshot;
        Changed?.Invoke(this, snapshot);
    }

    private static bool ApplyDoesNotRequireMacOsElevation(string? processPath)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(processPath);
        return !fullPath.StartsWith("/Applications/", StringComparison.Ordinal)
            && !fullPath.StartsWith(
                "/System/Applications/",
                StringComparison.Ordinal);
    }
}
