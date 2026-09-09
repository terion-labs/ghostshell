namespace Asura.Application;

public interface IRuntimeRecoveryStore
{
    ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>> LoadAsync(
        string runId,
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<Unit>> SaveAsync(
        RuntimeRecoverySnapshot snapshot,
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<Unit>> DiscardAsync(
        string runId,
        CancellationToken cancellationToken);
}
