using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Provider-neutral file operations owned by one hosted panel session. Implementations may share
/// provider runtimes, but transfers exposed through this boundary belong only to this session.
/// </summary>
public interface IFilePanelSession : IPanelSession
{
    FileSessionMetadata Metadata { get; }

    IReadOnlyList<FilePanelTransferSnapshot> Transfers { get; }

    ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<FilePanelResult<FilePanelEntry>> SearchAsync(
        FilePanelSearchRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelTextWriteReceipt>> WriteTextAsync(
        FilePanelTextWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = cancellationToken;
        return ValueTask.FromResult(FilePanelResult<FilePanelTextWriteReceipt>.Failure(
            FilePanelMutationErrors.Unsupported));
    }

    ValueTask<FilePanelResult<FilePanelCopyReceipt>> CopyAsync(
        FilePanelCopyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = cancellationToken;
        return ValueTask.FromResult(FilePanelResult<FilePanelCopyReceipt>.Failure(
            FilePanelMutationErrors.Unsupported));
    }

    /// <summary>
    /// Defaulted for the same reason the client's is: most connections have no
    /// notion of who can do what, and none should have to write out that they
    /// have none.
    /// </summary>
    ValueTask<FilePanelResult<FilePanelAccessControl>> GetAccessControlAsync(
        FilePanelAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = cancellationToken;
        return ValueTask.FromResult(FilePanelResult<FilePanelAccessControl>.Failure(
            FilePanelAccessControlErrors.Unsupported));
    }

    ValueTask<FilePanelResult<FilePanelAccessControl>> SetAccessControlAsync(
        FilePanelSetAccessControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = cancellationToken;
        return ValueTask.FromResult(FilePanelResult<FilePanelAccessControl>.Failure(
            FilePanelAccessControlErrors.Unsupported));
    }

    ValueTask<FilePanelResult<FilePanelTransferSnapshot>> EnqueueTransferAsync(
        FilePanelTransferRequest request,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<Unit>> CancelTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken);

    ValueTask<FilePanelResult<FilePanelTransferSnapshot>> RetryTransferAsync(
        FilePanelTransferId id,
        CancellationToken cancellationToken);
}
