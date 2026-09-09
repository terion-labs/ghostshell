namespace Asura.Application;

public enum OnboardingProgressErrorCode
{
    InvalidData,
    Conflict,
    StorageUnavailable,
    StorageFailure,
    Cancelled,
}

public sealed record OnboardingProgressError(
    OnboardingProgressErrorCode Code,
    string Message);

public sealed class OnboardingProgressResult<T>
{
    private OnboardingProgressResult(T? value, OnboardingProgressError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public OnboardingProgressError? Error { get; }

    public static OnboardingProgressResult<T> Success(T value) => new(value, null);

    public static OnboardingProgressResult<T> Failure(OnboardingProgressError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
