using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Files;

/// <summary>
/// Creates provider-neutral file-panel sessions over the existing direct clients. The clients stay
/// available for compatibility while each created session adds transfer ownership and lifecycle.
/// </summary>
public sealed class FilePanelSessionFactory : IFilePanelSessionFactory
{
    private readonly IFilePanelClient _filePanel;
    private readonly IFileTransferQueueClient _transferQueue;

    public FilePanelSessionFactory(
        IFilePanelClient filePanel,
        IFileTransferQueueClient transferQueue)
    {
        _filePanel = filePanel ?? throw new ArgumentNullException(nameof(filePanel));
        _transferQueue = transferQueue ?? throw new ArgumentNullException(nameof(transferQueue));
    }

    public CapabilitySet Capabilities { get; } = new(
    [
        SessionCapabilities.AttachRead,
        SessionCapabilities.FilesList,
        SessionCapabilities.FilesSearch,
        SessionCapabilities.FilesStat,
        SessionCapabilities.FilesPreview,
        SessionCapabilities.FilesReadAccessControl,
        SessionCapabilities.FilesTransfersRead,
        SessionCapabilities.FilesCreateDirectory,
        SessionCapabilities.FilesRename,
        SessionCapabilities.FilesDelete,
        GovernedFileToolNames.SessionWrite,
        GovernedFileToolNames.SessionCopy,
        SessionCapabilities.FilesTransferEnqueue,
        SessionCapabilities.FilesTransferCancel,
        SessionCapabilities.FilesTransferRetry,
    ]);

    public ValueTask<IFilePanelSession> CreateAsync(
        WorkspaceInstanceId workspaceId,
        SessionId sessionId,
        FilePanelLocation initialLocation,
        CancellationToken cancellationToken) =>
        CreateAsync(sessionId, initialLocation, cancellationToken);

    public ValueTask<IFilePanelSession> CreateAsync(
        SessionId sessionId,
        FilePanelLocation initialLocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialLocation);
        cancellationToken.ThrowIfCancellationRequested();
        IFilePanelClient sessionClient = _filePanel;
        IFileTransferQueueClient sessionTransferQueue = _transferQueue;
        IDisposable? ownedClient = null;
        try
        {
            if (_filePanel is CatalogFileProviderRuntime catalog)
            {
                var binding = catalog.AcquirePanelClientBinding(
                    initialLocation.ProviderProfileId);
                sessionClient = binding;
                sessionTransferQueue = binding;
                ownedClient = binding;
            }

            var profile = sessionClient.Profiles.SingleOrDefault(candidate =>
                string.Equals(
                    candidate.Id,
                    initialLocation.ProviderProfileId,
                    StringComparison.Ordinal)) ?? throw new ArgumentException(
                    "The initial file location references an unavailable provider profile.",
                    nameof(initialLocation));
            if (!string.Equals(
                    profile.Root.Authority,
                    initialLocation.Authority,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The initial file location authority does not match its provider profile.",
                    nameof(initialLocation));
            }

            if (!IsCompatibleInitialScope(profile, initialLocation))
            {
                throw new ArgumentException(
                    "The initial file location is outside or structurally "
                    + "incompatible with its provider profile root.",
                    nameof(initialLocation));
            }

            var metadata = new FileSessionMetadata(
                initialLocation,
                profile.Capabilities,
                profile.MaximumPageSize,
                profile.MaximumPreviewBytes);
            return ValueTask.FromResult<IFilePanelSession>(new FilePanelSession(
                sessionId,
                initialLocation,
                sessionClient,
                sessionTransferQueue,
                Capabilities,
                metadata,
                ownedClient));
        }
        catch
        {
            ownedClient?.Dispose();
            throw;
        }
    }

    private static bool StartsWith(FilePanelPath path, FilePanelPath prefix)
    {
        if (path.Segments.Length < prefix.Segments.Length)
        {
            return false;
        }

        return path.Segments
            .Take(prefix.Segments.Length)
            .SequenceEqual(prefix.Segments);
    }

    private static bool IsCompatibleInitialScope(
        FileProviderProfileDescriptor profile,
        FilePanelLocation initialLocation)
    {
        // S3 deliberately supports container, hierarchical-prefix, and exact
        // object-key locations in one profile. Its configured Root is an
        // initial browser location rather than a credential boundary, so a
        // recovered child location must not be rejected by address shape.
        if (profile.Family == FileProviderFamily.S3)
        {
            return true;
        }

        return (profile.Root.Address, initialLocation.Address) switch
        {
            (
                FilePanelAddress.Hierarchical profilePath,
                FilePanelAddress.Hierarchical initial) =>
                StartsWith(initial.Path, profilePath.Path),
            (
                FilePanelAddress.ObjectKey profileObject,
                FilePanelAddress.ObjectKey initial) =>
                string.Equals(
                    profileObject.Key,
                    initial.Key,
                    StringComparison.Ordinal),
            (
                FilePanelAddress.ContainerRoot,
                FilePanelAddress.ContainerRoot) => true,
            _ => false,
        };
    }
}
