namespace Asura.Application;

/// <summary>
/// A stable, content-free monitoring failure. Native exception text, process names, and command
/// lines must not cross this boundary.
/// </summary>
public sealed record MonitorPanelError(
    MonitorPanelErrorCode Code,
    string StableCode,
    string Message,
    bool Retryable)
{
    public static MonitorPanelError Create(MonitorPanelErrorCode code) => code switch
    {
        MonitorPanelErrorCode.InvalidQuery =>
            New(code, "monitor_invalid_query", "The monitoring query is invalid.", false),
        MonitorPanelErrorCode.Unavailable =>
            New(code, "monitor_unavailable", "System monitoring is unavailable.", true),
        MonitorPanelErrorCode.AccessDenied =>
            New(code, "monitor_access_denied", "The operating system denied monitoring access.", false),
        MonitorPanelErrorCode.CaptureFailed =>
            New(code, "monitor_capture_failed", "The system snapshot could not be captured.", true),
        MonitorPanelErrorCode.SessionClosed =>
            New(code, "monitor_session_closed", "The monitoring session is closed.", false),
        MonitorPanelErrorCode.Cancelled =>
            New(code, "monitor_cancelled", "The monitoring operation was cancelled.", false),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };

    private static MonitorPanelError New(
        MonitorPanelErrorCode code,
        string stableCode,
        string message,
        bool retryable) =>
        new(code, stableCode, message, retryable);
}
