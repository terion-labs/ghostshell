namespace Asura.Application;

public enum ConnectionRuntimeErrorCode
{
    InvalidProfile,
    AdapterUnavailable,
    RuntimeMissing,
    UnsupportedPlatform,
    SecretVaultUnavailable,
    SecretNotFound,
    SecretAccessDenied,
    SecretInvalid,
    SecretVaultFailure,
    AuthenticationRequired,
    AuthenticationFailed,
    UnknownHostKey,
    HostKeyChanged,
    HostKeyReviewExpired,
    PermissionDenied,
    Timeout,
    Offline,
    ContainerNotFound,
    DistributionNotFound,
    Cancelled,
    ProcessFailed,
}
