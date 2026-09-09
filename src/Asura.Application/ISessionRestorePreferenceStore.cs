namespace Asura.Application;

/// <summary>
/// Persists whether this local profile should reopen its latest runtime session
/// after a normal application start. The preference is local rather than part of
/// an exported workspace definition.
/// </summary>
public interface ISessionRestorePreferenceStore
{
    ValueTask<ApplicationRunResult<bool>> ReadAsync(
        CancellationToken cancellationToken);

    ValueTask<ApplicationRunResult<Unit>> WriteAsync(
        bool restoreSessionsOnStart,
        CancellationToken cancellationToken);
}
