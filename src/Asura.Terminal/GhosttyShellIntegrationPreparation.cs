using Asura.Application;
using Asura.Core;

namespace Asura.Terminal;

internal enum GhosttyShellIntegrationPreparationStatus
{
    Disabled,
    NotDetected,
    Applied,
    UnsupportedShell,
    IncompatibleLaunch,
    ResourcesUnavailable,
}

/// <summary>
/// Describes the process-only launch mutation used to inject Ghostty's shell
/// scripts. The original launch remains the durable session identity.
/// </summary>
internal sealed record GhosttyShellIntegrationPreparation(
    TerminalLaunchRequest Launch,
    GhosttyShellIntegrationPreparationStatus Status,
    TerminalShellIntegrationMode? Shell,
    string? Detail)
{
    public bool IsApplied => Status == GhosttyShellIntegrationPreparationStatus.Applied;
}
