namespace Asura.Files;

internal enum RemoteFileSessionErrorCode
{
    InvalidConfiguration,
    AuthenticationFailed,
    HostKeyUnknown,
    HostKeyChanged,
    HostKeyStoreInvalid,
    CertificateRejected,
    SecureTransportUnavailable,
    NotFound,
    AlreadyExists,
    AccessDenied,
    NotDirectory,
    IsDirectory,
    DirectoryNotEmpty,
    LimitExceeded,
    LinkNotAllowed,
    Unsupported,
    InvalidName,
    Transient,
    IoFailure,
}

/// <summary>
/// Sanitized protocol-boundary failure. Vendor messages may contain usernames, paths, or server
/// text, so adapters classify them without forwarding those messages into provider results.
/// </summary>
internal sealed class RemoteFileSessionException : Exception
{
    public RemoteFileSessionException(
        RemoteFileSessionErrorCode code,
        string safeMessage,
        bool retryable = false,
        Exception? innerException = null)
        : base(safeMessage, innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public RemoteFileSessionErrorCode Code { get; }

    public bool Retryable { get; }

}
