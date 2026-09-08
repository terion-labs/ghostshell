using GhostShell.Application;

namespace GhostShell.Terminal;

/// <summary>
/// Describes the local process exit without treating remote-controlled PTY text as diagnostics.
/// </summary>
internal static class TerminalProcessExitDescription
{
    public static string Describe(
        TerminalLaunchRequest launch,
        int? exitCode)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (!IsSsh(launch))
        {
            return ExitCode("terminal", exitCode);
        }

        if (exitCode is null or 0)
        {
            return exitCode == 0
                ? "The SSH session ended normally."
                : "The SSH session ended.";
        }

        return ExitCode("OpenSSH", exitCode);
    }

    private static bool IsSsh(TerminalLaunchRequest launch) =>
        launch.ConnectionMetadata?.ConnectionBoundary.StartsWith(
            "SSH:",
            StringComparison.OrdinalIgnoreCase) == true;

    private static string ExitCode(string process, int? exitCode) =>
        exitCode is { } known
            ? $"The {process} process exited with code {known}."
            : $"The {process} process exited.";
}
