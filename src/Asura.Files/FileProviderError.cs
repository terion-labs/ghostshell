namespace Asura.Files;

public sealed record FileProviderError(
    FileProviderErrorCode Code,
    string StableCode,
    string Message,
    bool Retryable = false)
{
    public static FileProviderError Create(
        FileProviderErrorCode code,
        string message,
        bool retryable = false) =>
        new(code, GetStableCode(code), message, retryable);

    private static string GetStableCode(FileProviderErrorCode code) => code switch
    {
        FileProviderErrorCode.UnsupportedCapability => "unsupported_capability",
        FileProviderErrorCode.InvalidLocation => "invalid_location",
        FileProviderErrorCode.InvalidName => "invalid_name",
        FileProviderErrorCode.OutsideRoot => "outside_root",
        FileProviderErrorCode.RootMutationNotAllowed => "root_mutation_not_allowed",
        FileProviderErrorCode.NotFound => "not_found",
        FileProviderErrorCode.AlreadyExists => "already_exists",
        FileProviderErrorCode.Conflict => "conflict",
        FileProviderErrorCode.PreconditionFailed => "precondition_failed",
        FileProviderErrorCode.RangeNotSatisfiable => "range_not_satisfiable",
        FileProviderErrorCode.LimitExceeded => "limit_exceeded",
        FileProviderErrorCode.AuthenticationRequired => "authentication_required",
        FileProviderErrorCode.AccessDenied => "access_denied",
        FileProviderErrorCode.HostKeyUnknown => "host_key_unknown",
        FileProviderErrorCode.HostKeyChanged => "host_key_changed",
        FileProviderErrorCode.HostKeyStoreInvalid => "host_key_store_invalid",
        FileProviderErrorCode.NotDirectory => "not_directory",
        FileProviderErrorCode.IsDirectory => "is_directory",
        FileProviderErrorCode.DirectoryNotEmpty => "directory_not_empty",
        FileProviderErrorCode.LinkNotAllowed => "link_not_allowed",
        FileProviderErrorCode.SharingViolation => "sharing_violation",
        FileProviderErrorCode.QuotaExceeded => "quota_exceeded",
        FileProviderErrorCode.UnexpectedEndOfStream => "unexpected_end_of_stream",
        FileProviderErrorCode.PartialTransfer => "partial_transfer",
        FileProviderErrorCode.Cancelled => "cancelled",
        FileProviderErrorCode.IoFailure => "io_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };
}
