namespace Asura.Application.ApplicationUpdates;

/// <summary>
/// Reports distributions whose updates are unavailable to this process. Store
/// and package-manager builds stay passive because their installer owns updates.
/// </summary>
public sealed class PassiveApplicationUpdateService : IApplicationUpdateService
{
    public PassiveApplicationUpdateService(DistributionIdentity distribution)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        Snapshot = new(
            distribution,
            distribution.UpdateStrategy == ApplicationUpdateStrategy.PlatformManaged
                ? ApplicationUpdateStage.ManagedExternally
                : ApplicationUpdateStage.Unavailable);
    }

    public event EventHandler<ApplicationUpdateSnapshot>? Changed
    {
        add { }
        remove { }
    }

    public ApplicationUpdateSnapshot Snapshot { get; }

    public Task CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task DownloadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void RestartToApply()
    {
    }
}
