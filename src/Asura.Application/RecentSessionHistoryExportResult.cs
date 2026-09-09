namespace Asura.Application;

public enum RecentSessionHistoryExportErrorCode
{
    InvalidHistory,
    TooManyRecords,
    DestinationUnavailable,
    CleanupFailure,
    Cancelled,
}

public sealed record RecentSessionHistoryExportError(
    RecentSessionHistoryExportErrorCode Code,
    string Message);

public sealed class RecentSessionHistoryExportResult<T>
{
    private RecentSessionHistoryExportResult(
        T? value,
        RecentSessionHistoryExportError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public RecentSessionHistoryExportError? Error { get; }

    public static RecentSessionHistoryExportResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    public static RecentSessionHistoryExportResult<T> Failure(
        RecentSessionHistoryExportError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
