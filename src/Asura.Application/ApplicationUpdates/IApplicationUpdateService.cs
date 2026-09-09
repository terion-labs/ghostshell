namespace Asura.Application.ApplicationUpdates;

public interface IApplicationUpdateService
{
    event EventHandler<ApplicationUpdateSnapshot>? Changed;

    ApplicationUpdateSnapshot Snapshot { get; }

    Task CheckAsync(CancellationToken cancellationToken);

    Task DownloadAsync(CancellationToken cancellationToken);

    void RestartToApply();
}
