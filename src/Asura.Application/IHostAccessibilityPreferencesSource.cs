using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Supplies the current host accessibility preferences and reports observed changes.
/// Change notifications may be raised from a background thread.
/// </summary>
public interface IHostAccessibilityPreferencesSource : IDisposable
{
    HostAccessibilityPreferences Current { get; }

    event EventHandler? Changed;

    /// <summary>
    /// Starts observing the host. Implementations must make this operation idempotent.
    /// </summary>
    void Start();
}
