namespace Asura.Application;

public enum HostErrorCode
{
    InvalidRequest,
    NotFound,
    RevisionConflict,
    UnsupportedProtocol,
    CapabilityNotSupported,
    ConfirmationRequired,
    LeaseDenied,
    IdempotencyKeyReused,
    DeadlineExceeded,
    Cancelled,
    SessionClosed,
    EngineFailed,
    ResynchronizationRequired,
}
