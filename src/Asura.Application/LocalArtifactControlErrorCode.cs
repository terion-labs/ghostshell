namespace Asura.Application;

public enum LocalArtifactControlErrorCode
{
    UnsupportedArtifactKind,
    LimitExceeded,
    UnsafeLayout,
    AccessDenied,
    Unavailable,
    IoFailure,
    PartialRemoval,
    Cancelled,
}
