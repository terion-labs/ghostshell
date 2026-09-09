namespace Asura.Application;

public enum AuditStoreErrorCode
{
    InvalidEvent = 0,
    Conflict = 1,
    StorageUnavailable = 2,
    StorageFailure = 3,
    Cancelled = 4,
    InvalidQuery = 5,
}

public enum AgentActionAuditClaimOutcome
{
    Claimed,
    AlreadyClaimed,
}

public sealed record AuditStoreError(AuditStoreErrorCode Code, string Message);

public sealed class AuditStoreResult<T>
{
    private AuditStoreResult(T? value, AuditStoreError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public AuditStoreError? Error { get; }

    public static AuditStoreResult<T> Success(T value) => new(value, null);

    public static AuditStoreResult<T> Failure(AuditStoreError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
