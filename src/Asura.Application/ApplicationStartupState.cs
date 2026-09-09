namespace Asura.Application;

public sealed class ApplicationStartupState
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when the whole profile is ready — run marker, catalog,
    /// preferences — not merely when this state is filled in. Session restore
    /// resolves connections against the catalog, so a signal that fired before
    /// the catalog loaded restored workspaces whose every adapter was
    /// "unavailable". With keys sealed under the startup PIN all of this happens
    /// behind the lock screen, not before the window.
    /// </summary>
    public Task Initialized => _initialized.Task;

    /// <summary>Called once by startup after the last initialization step.</summary>
    public void MarkProfileInitialized() => _initialized.TrySetResult();

    public ApplicationRunStart? Run { get; private set; }

    /// <summary>
    /// Whether the previous run ended without writing its clean-shutdown marker.
    /// Reportable, and nothing more: the runtime snapshot is written as the
    /// workspace changes, so what the window comes back to does not depend on
    /// how the last process ended.
    /// </summary>
    public bool PreviousRunWasInterrupted
    {
        get
        {
            lock (_gate)
            {
                return Run is { RecoveryRequired: true };
            }
        }
    }

    public void Initialize(ApplicationRunStart run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.RecoveryRequired && string.IsNullOrWhiteSpace(run.PreviousState.RunId))
        {
            throw new ArgumentException(
                "An interrupted startup must identify the interrupted run.",
                nameof(run));
        }

        lock (_gate)
        {
            if (Run is not null)
            {
                throw new InvalidOperationException("Application startup state is already initialized.");
            }

            Run = run;
        }
    }
}
