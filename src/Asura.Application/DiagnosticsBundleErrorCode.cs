namespace Asura.Application;

public enum DiagnosticsBundleErrorCode
{
    InvalidRequest,
    TooManyArtifacts,
    ArtifactTooLarge,
    BundleTooLarge,
    InvalidPath,
    DuplicatePath,
    UnsafeContent,
    DestinationUnavailable,
    Cancelled,
}
