namespace Asura.Files;

/// <summary>
/// Provider-neutral filesystem operations. Streams remain owned by the caller. Implementations
/// must honor request bounds, return typed expected failures, and never claim an optional
/// capability whose semantics they do not implement.
/// </summary>
public interface IFileProvider
{
    FileProviderProfileId ProfileId { get; }

    FileProviderCapabilities Capabilities { get; }

    ValueTask<FileProviderResult<FilePage>> ListAsync(
        FileListRequest request,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileEntry>> StatAsync(
        FileStatRequest request,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(
        FileReadRequest request,
        Stream destination,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(
        FileWriteRequest request,
        Stream source,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(
        FileCreateDirectoryRequest request,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileEntry>> RenameAsync(
        FileRenameRequest request,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(
        FileTransferRequest request,
        IProgress<FileTransferProgress>? progress,
        CancellationToken cancellationToken);

    ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(
        FileDeleteRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Who can do what to one item. Answered by a provider that declares
    /// <see cref="FileProviderCapability.Permissions"/> or
    /// <see cref="FileProviderCapability.AccessControlLists"/>; the rest say so
    /// rather than inventing an answer, which is what the default here does.
    ///
    /// It is a default implementation because most providers have no such
    /// notion, and a filesystem that cannot describe permissions should not have
    /// to write out that it cannot.
    /// </summary>
    ValueTask<FileProviderResult<FileAccessControl>> GetAccessControlAsync(
        FileAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = cancellationToken;
        return ValueTask.FromResult(FileProviderResult<FileAccessControl>.Failure(
            FileProviderErrors.UnsupportedAccessControl));
    }

    ValueTask<FileProviderResult<FileAccessControl>> SetAccessControlAsync(
        FileSetAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = cancellationToken;
        return ValueTask.FromResult(FileProviderResult<FileAccessControl>.Failure(
            FileProviderErrors.UnsupportedAccessControl));
    }
}

internal static class FileProviderErrors
{
    public static FileProviderError UnsupportedAccessControl { get; } =
        FileProviderError.Create(
            FileProviderErrorCode.UnsupportedCapability,
            "This connection does not describe who can read or change its files.");
}
