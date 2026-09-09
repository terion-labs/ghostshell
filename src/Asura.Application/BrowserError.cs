namespace Asura.Application;

public sealed record BrowserError
{
    public const int MaximumMessageLength = 2_048;

    private BrowserError(
        BrowserErrorCode code,
        string message,
        bool retryable)
    {
        Code = code;
        StableCode = ToStableCode(code);
        Message = message;
        Retryable = retryable;
    }

    public BrowserErrorCode Code { get; }

    public string StableCode { get; }

    public string Message { get; }

    public bool Retryable { get; }

    public static BrowserError Create(
        BrowserErrorCode code,
        string message,
        bool retryable = false)
    {
        if (!Enum.IsDefined(code))
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > MaximumMessageLength
            || message.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A browser error message must be NUL-free and at most {MaximumMessageLength} characters.",
                nameof(message));
        }

        return new BrowserError(code, message, retryable);
    }

    private static string ToStableCode(BrowserErrorCode code) => code switch
    {
        BrowserErrorCode.UnsupportedCapability => "unsupported_capability",
        BrowserErrorCode.RendererUnavailable => "renderer_unavailable",
        BrowserErrorCode.HistoryUnavailable => "history_unavailable",
        BrowserErrorCode.NavigationInProgress => "navigation_in_progress",
        BrowserErrorCode.NavigationStateChanged => "browser_state_changed",
        BrowserErrorCode.NavigationPolicyDenied =>
            "browser_domain_policy_denied",
        BrowserErrorCode.SnapshotInvalid => "browser_snapshot_invalid",
        BrowserErrorCode.ElementReferenceStale =>
            "browser_element_reference_stale",
        BrowserErrorCode.ElementNotInteractable =>
            "browser_element_not_interactable",
        BrowserErrorCode.ElementNotFillable =>
            "browser_element_not_fillable",
        BrowserErrorCode.FillValueNotSupported =>
            "browser_fill_value_not_supported",
        BrowserErrorCode.ElementNotCheckable =>
            "browser_element_not_checkable",
        BrowserErrorCode.CheckStateNotApplied =>
            "browser_check_state_not_applied",
        BrowserErrorCode.ScriptRejected => "browser_script_rejected",
        BrowserErrorCode.ScriptResultRejected =>
            "browser_script_result_rejected",
        BrowserErrorCode.InteractionOutcomeUnknown =>
            "browser_interaction_outcome_unknown",
        BrowserErrorCode.NavigationFailed => "navigation_failed",
        BrowserErrorCode.NetworkUnavailable => "network_unavailable",
        BrowserErrorCode.NavigationTimedOut => "navigation_timed_out",
        BrowserErrorCode.CertificateRejected => "certificate_rejected",
        BrowserErrorCode.SessionClosed => "session_closed",
        BrowserErrorCode.Cancelled => "cancelled",
        BrowserErrorCode.EngineFailed => "engine_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}
