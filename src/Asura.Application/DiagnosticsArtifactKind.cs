namespace Asura.Application;

/// <summary>
/// Text-only artifact categories that may cross the diagnostics export boundary.
/// Terminal, command, credential, environment, and secret-bearing categories are deliberately absent.
/// </summary>
public enum DiagnosticsArtifactKind
{
    ApplicationLog = 1,
    CrashReport = 2,
    ComponentStatus = 3,
    PerformanceSummary = 4,
}
