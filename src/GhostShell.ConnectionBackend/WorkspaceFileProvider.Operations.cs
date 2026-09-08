using GhostShell.Files;

namespace GhostShell.ConnectionBackend;

internal sealed partial class WorkspaceFileProvider
{
    public ValueTask<FileProviderResult<FilePage>> ListAsync(FileListRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(new(profile, connection, FileWorkspaceOperation.List, List: request),
            response => response.Page is { } page ? new FilePage(page.Items, page.ContinuationToken) : null, cancellationToken);

    public ValueTask<FileProviderResult<FileEntry>> StatAsync(FileStatRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(new(profile, connection, FileWorkspaceOperation.Stat, Stat: request), response => response.Entry, cancellationToken);

    public ValueTask<FileProviderResult<FileReadReceipt>> ReadAsync(FileReadRequest request, Stream destination,
        IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken) =>
        InvokeAsync(new(profile, connection, FileWorkspaceOperation.Read, Read: request), response => response.Read,
            cancellationToken, download: destination, progress: progress);

    public ValueTask<FileProviderResult<FileWriteReceipt>> WriteAsync(FileWriteRequest request, Stream source,
        IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken) =>
        InvokeAsync(new(profile, connection, FileWorkspaceOperation.Write, Write: request), response => response.Write,
            cancellationToken, upload: source, progress: progress);

    public ValueTask<FileProviderResult<FileEntry>> CreateDirectoryAsync(FileCreateDirectoryRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(new(profile, connection, FileWorkspaceOperation.CreateDirectory, CreateDirectory: request), response => response.Entry, cancellationToken);

    public ValueTask<FileProviderResult<FileEntry>> RenameAsync(FileRenameRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(new(profile, connection, FileWorkspaceOperation.Rename, Rename: request), response => response.Entry, cancellationToken);

    public ValueTask<FileProviderResult<FileTransferReceipt>> TransferAsync(FileTransferRequest request,
        IProgress<FileTransferProgress>? progress, CancellationToken cancellationToken) =>
        InvokeAsync(new(profile, connection, FileWorkspaceOperation.Transfer, Transfer: request), response => response.Transfer,
            cancellationToken, progress: progress);

    public ValueTask<FileProviderResult<FileDeleteReceipt>> DeleteAsync(FileDeleteRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(new(profile, connection, FileWorkspaceOperation.Delete, Delete: request), response => response.Delete, cancellationToken);

    public ValueTask<FileProviderResult<FileAccessControl>> GetAccessControlAsync(FileAccessControlRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(new(profile, connection, FileWorkspaceOperation.GetAccessControl, GetAccessControl: request), response => response.AccessControl, cancellationToken);

    public ValueTask<FileProviderResult<FileAccessControl>> SetAccessControlAsync(FileSetAccessControlRequest request, CancellationToken cancellationToken) =>
        InvokeAsync(new(profile, connection, FileWorkspaceOperation.SetAccessControl, SetAccessControl: request), response => response.AccessControl, cancellationToken);
}
