namespace Asura.Application;

public enum SecretVaultErrorCode
{
    InvalidRequest,
    Unavailable,
    NotFound,
    AlreadyExists,
    AccessDenied,
    AuthenticationRequired,
    UserCancelled,
    CorruptEntry,
    PlatformFailure,
    AuditPersistenceFailure,
    Cancelled,
}
