namespace Asura.Application;

/// <summary>
/// Owns startup-session selection while leaving snapshot validation and storage
/// to the existing recovery boundaries.
/// </summary>
public sealed class SessionRestoreCoordinator
{
    private readonly ISessionRestorePreferenceStore _preferenceStore;
    private readonly IRuntimeRecoveryStore _recoveryStore;
    private readonly IRuntimeRecoveryDataControl _recoveryData;

    public SessionRestoreCoordinator(
        ISessionRestorePreferenceStore preferenceStore,
        IRuntimeRecoveryStore recoveryStore,
        IRuntimeRecoveryDataControl recoveryData)
    {
        _preferenceStore = preferenceStore
            ?? throw new ArgumentNullException(nameof(preferenceStore));
        _recoveryStore = recoveryStore
            ?? throw new ArgumentNullException(nameof(recoveryStore));
        _recoveryData = recoveryData
            ?? throw new ArgumentNullException(nameof(recoveryData));
    }

    public ValueTask<ApplicationRunResult<bool>> ReadPreferenceAsync(
        CancellationToken cancellationToken) =>
        _preferenceStore.ReadAsync(cancellationToken);

    public ValueTask<ApplicationRunResult<Unit>> WritePreferenceAsync(
        bool restoreSessionsOnStart,
        CancellationToken cancellationToken) =>
        _preferenceStore.WriteAsync(restoreSessionsOnStart, cancellationToken);

    public async ValueTask<ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>>
        LoadLatestSessionAsync(CancellationToken cancellationToken)
    {
        var inventory = await _recoveryData.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!inventory.IsSuccess)
        {
            return ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>.Failure(
                inventory.Error!);
        }

        var latest = inventory.Value!.Runs
            .OrderByDescending(run => run.LastUpdatedAt)
            .ThenByDescending(run => run.RunId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest is null)
        {
            return ApplicationRunResult<IReadOnlyList<RuntimeRecoverySnapshot>>.Success([]);
        }

        return await _recoveryStore.LoadAsync(latest.RunId, cancellationToken)
            .ConfigureAwait(false);
    }
}
