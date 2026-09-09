namespace Asura.Application;

public enum FilePanelErrorCode
{
    UnknownProfile,
    UnsupportedCapability,
    InvalidLocation,
    InvalidName,
    OutsideRoot,
    RootMutationNotAllowed,
    NotFound,
    AlreadyExists,
    Conflict,
    PreconditionFailed,
    RangeNotSatisfiable,
    LimitExceeded,
    AccessDenied,
    NotDirectory,
    IsDirectory,
    DirectoryNotEmpty,
    LinkNotAllowed,
    SharingViolation,
    QuotaExceeded,
    UnexpectedEndOfStream,
    PartialTransfer,
    Cancelled,
    Offline,
    AuthenticationRequired,
    CertificateRejected,
    HostKeyRejected,
    HostKeyUnknown,
    HostKeyChanged,
    HostKeyStoreInvalid,
    IoFailure,
}

public sealed record FilePanelError(
    FilePanelErrorCode Code,
    string StableCode,
    string Message,
    bool Retryable);

public sealed class FilePanelResult<T>
{
    private FilePanelResult(T? value, FilePanelError? error)
    {
        Value = value;
        Error = error;
    }

    public bool IsSuccess => Error is null;

    public T? Value { get; }

    public FilePanelError? Error { get; }

    public static FilePanelResult<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    public static FilePanelResult<T> Failure(FilePanelError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}
