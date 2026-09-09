namespace Asura.Application;

public sealed record ApplicationRunState(
    string? RunId,
    bool WasClean,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastCleanAt);

public sealed record ApplicationRunStart(
    string RunId,
    bool RecoveryRequired,
    ApplicationRunState PreviousState);

public enum ApplicationRunErrorCode
{
    RunMismatch,
    StorageUnavailable,
    StorageFailure,
    Cancelled,
}

public sealed record ApplicationRunError(ApplicationRunErrorCode Code, string Message);
