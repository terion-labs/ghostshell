namespace Asura.Application;

public sealed record HostError(
    HostErrorCode Code,
    string StableCode,
    string Message,
    bool Retryable = false)
{
    public static HostError Create(
        HostErrorCode code,
        string message,
        bool retryable = false) =>
        new(code, ToStableCode(code), message, retryable);

    private static string ToStableCode(HostErrorCode code) => code switch
    {
        HostErrorCode.InvalidRequest => "invalid_request",
        HostErrorCode.NotFound => "not_found",
        HostErrorCode.RevisionConflict => "revision_conflict",
        HostErrorCode.UnsupportedProtocol => "unsupported_protocol",
        HostErrorCode.CapabilityNotSupported => "capability_not_supported",
        HostErrorCode.ConfirmationRequired => "confirmation_required",
        HostErrorCode.LeaseDenied => "lease_denied",
        HostErrorCode.IdempotencyKeyReused => "idempotency_key_reused",
        HostErrorCode.DeadlineExceeded => "deadline_exceeded",
        HostErrorCode.Cancelled => "cancelled",
        HostErrorCode.SessionClosed => "session_closed",
        HostErrorCode.EngineFailed => "engine_failed",
        HostErrorCode.ResynchronizationRequired => "resync_required",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}
