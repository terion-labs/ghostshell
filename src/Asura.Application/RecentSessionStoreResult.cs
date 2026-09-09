namespace Asura.Application;

public enum RecentSessionStoreErrorCode
{
    InvalidRecord,
    InvalidHistoryData,
    InvalidRetentionData,
    Conflict,
    StorageUnavailable,
    StorageFailure,
    Cancelled,
}

public sealed record RecentSessionStoreError(
    RecentSessionStoreErrorCode Code,
    string Message);

public sealed class RecentSessionStoreResult<T>
{
    private RecentSessionStoreResult(T? value, RecentSessionStoreError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public RecentSessionStoreError? Error { get; }

    public static RecentSessionStoreResult<T> Success(T value) => new(value, null);

    public static RecentSessionStoreResult<T> Failure(RecentSessionStoreError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
