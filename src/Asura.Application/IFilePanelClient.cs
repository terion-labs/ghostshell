namespace Asura.Application;

/// <summary>
/// Presentation-safe file operations. Concrete providers and their SDK payloads stay behind
/// the desktop composition boundary.
/// </summary>
public interface IFilePanelClient
{
    IReadOnlyList<FileProviderProfileDescriptor> Profiles { get; }

    ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Searches through provider listings rather than filtering rows already materialized by a
    /// view. The default implementation gives every protocol the same complete traversal.
    /// </summary>
    IAsyncEnumerable<FilePanelResult<FilePanelEntry>> SearchAsync(
        FilePanelSearchRequest request,
        CancellationToken cancellationToken) =>
        FilePanelSearch.FindAsync(this, request, cancellationToken);

    /// <summary>
    /// Observes provider contents through the same listing boundary. Polling is the portable
    /// baseline for remote protocols; an implementation may replace it with native notifications.
    /// </summary>
    IAsyncEnumerable<FilePanelResult<FilePanelChange>> WatchAsync(
        FilePanelWatchRequest request,
        CancellationToken cancellationToken) =>
        FilePanelWatch.ObserveAsync(this, request, cancellationToken);

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
    /// Who can do what to one item. A connection that does not describe such a
    /// thing says so rather than inventing an answer, which is what this
    /// default does — most of them cannot, and none should have to write out
    /// that it cannot.
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
}

public static class FilePanelMutationErrors
{
    public static FilePanelError Unsupported { get; } = new(
        FilePanelErrorCode.UnsupportedCapability,
        "file_mutation_unsupported",
        "This connection does not support this file mutation.",
        Retryable: false);
}

public static class FilePanelAccessControlErrors
{
    public static FilePanelError Unsupported { get; } = new(
        FilePanelErrorCode.UnsupportedCapability,
        "file_access_control_unsupported",
        "This connection does not describe who can read or change its files.",
        Retryable: false);
}
