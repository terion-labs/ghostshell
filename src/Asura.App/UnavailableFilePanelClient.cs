using Asura.Application;

namespace Asura.App;

/// <summary>
/// Represents an explicitly unavailable File Viewer. It deliberately exposes no direct provider
/// fallback, so a panel that cannot be assigned a hosted session cannot mutate files outside the
/// SessionHost boundary.
/// </summary>
internal sealed class UnavailableFilePanelClient : IFilePanelClient
{
    public static UnavailableFilePanelClient Instance { get; } = new();

    private UnavailableFilePanelClient()
    {
    }

    public IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; } = [];

    public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken) => Unavailable<FilePanelPage>(cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken) => Unavailable<FilePanelEntry>(cancellationToken);

    public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken) => Unavailable<FilePanelPreview>(cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken) => Unavailable<FilePanelEntry>(cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken) => Unavailable<FilePanelEntry>(cancellationToken);

    public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken) => Unavailable<FilePanelDeleteReceipt>(cancellationToken);

    public ValueTask<FilePanelResult<FilePanelTextWriteReceipt>> WriteTextAsync(
        FilePanelTextWriteRequest request,
        CancellationToken cancellationToken) =>
        Unavailable<FilePanelTextWriteReceipt>(cancellationToken);

    public ValueTask<FilePanelResult<FilePanelCopyReceipt>> CopyAsync(
        FilePanelCopyRequest request,
        CancellationToken cancellationToken) =>
        Unavailable<FilePanelCopyReceipt>(cancellationToken);

    private static ValueTask<FilePanelResult<T>> Unavailable<T>(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<FilePanelResult<T>>(cancellationToken);
        }

        return ValueTask.FromResult(FilePanelResult<T>.Failure(new FilePanelError(
            FilePanelErrorCode.Offline,
            "file_session_unavailable",
            "No hosted file-provider session is available for this panel.",
            Retryable: false)));
    }
}
