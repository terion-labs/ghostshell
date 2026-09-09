namespace Asura.Application;

public interface IApplicationRunStore
{
    ValueTask<ApplicationRunResult<ApplicationRunStart>> BeginRunAsync(
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<Unit>> CompleteRunAsync(
        string runId,
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<ApplicationRunState>> GetStateAsync(
        CancellationToken cancellationToken);
}
