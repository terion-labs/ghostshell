using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Desktop;

/// <summary>
/// Keeps the isolated workspace filesystem alongside routed remote providers.
/// Local operations stay inside the isolate; every catalog provider uses the
/// workspace connector supplied to its adapter runtime.
/// </summary>
internal sealed class WorkspaceFilePanelClient(
    IFilePanelClient workspaceFiles,
    IFilePanelClient routedProviders) : IFilePanelClient
{
    private readonly HashSet<string> _workspaceProfileIds = workspaceFiles.Profiles
        .Select(profile => profile.Id)
        .ToHashSet(StringComparer.Ordinal);

    public IReadOnlyList<FileProviderProfileDescriptor> Profiles =>
        [.. workspaceFiles.Profiles, .. routedProviders.Profiles.Where(profile =>
            !_workspaceProfileIds.Contains(profile.Id)
            && !string.Equals(
                profile.Id,
                BuiltInFileProviders.HomeId.Value,
                StringComparison.Ordinal))];

    public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(
        FilePanelListRequest request,
        CancellationToken cancellationToken) =>
        ClientFor(request.Location).ListAsync(request, cancellationToken);

    public IAsyncEnumerable<FilePanelResult<FilePanelEntry>> SearchAsync(
        FilePanelSearchRequest request,
        CancellationToken cancellationToken) =>
        ClientFor(request.Location).SearchAsync(request, cancellationToken);

    public IAsyncEnumerable<FilePanelResult<FilePanelChange>> WatchAsync(
        FilePanelWatchRequest request,
        CancellationToken cancellationToken) =>
        ClientFor(request.Location).WatchAsync(request, cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(
        FilePanelLocation location,
        CancellationToken cancellationToken) =>
        ClientFor(location).StatAsync(location, cancellationToken);

    public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(
        FilePanelPreviewRequest request,
        CancellationToken cancellationToken) =>
        ClientFor(request.Location).PreviewAsync(request, cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(
        FilePanelCreateDirectoryRequest request,
        CancellationToken cancellationToken) =>
        ClientFor(request.Location).CreateDirectoryAsync(request, cancellationToken);

    public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(
        FilePanelRenameRequest request,
        CancellationToken cancellationToken) =>
        SameClient(request.Source, request.Destination, out var client)
            ? client.RenameAsync(request, cancellationToken)
            : Unsupported<FilePanelEntry>();

    public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(
        FilePanelDeleteRequest request,
        CancellationToken cancellationToken) =>
        ClientFor(request.Location).DeleteAsync(request, cancellationToken);

    public ValueTask<FilePanelResult<FilePanelTextWriteReceipt>> WriteTextAsync(
        FilePanelTextWriteRequest request,
        CancellationToken cancellationToken) =>
        ClientFor(request.Location).WriteTextAsync(request, cancellationToken);

    public ValueTask<FilePanelResult<FilePanelCopyReceipt>> CopyAsync(
        FilePanelCopyRequest request,
        CancellationToken cancellationToken) =>
        SameClient(request.Source, request.Destination, out var client)
            ? client.CopyAsync(request, cancellationToken)
            : Unsupported<FilePanelCopyReceipt>();

    public ValueTask<FilePanelResult<FilePanelAccessControl>> GetAccessControlAsync(
        FilePanelAccessControlRequest request,
        CancellationToken cancellationToken) =>
        ClientFor(request.Location).GetAccessControlAsync(request, cancellationToken);

    public ValueTask<FilePanelResult<FilePanelAccessControl>> SetAccessControlAsync(
        FilePanelSetAccessControlRequest request,
        CancellationToken cancellationToken) =>
        ClientFor(request.Location).SetAccessControlAsync(request, cancellationToken);

    private IFilePanelClient ClientFor(FilePanelLocation location) =>
        _workspaceProfileIds.Contains(location.ProviderProfileId)
            ? workspaceFiles
            : routedProviders;

    private bool SameClient(
        FilePanelLocation source,
        FilePanelLocation destination,
        out IFilePanelClient client)
    {
        client = ClientFor(source);
        return ReferenceEquals(client, ClientFor(destination));
    }

    private static ValueTask<FilePanelResult<T>> Unsupported<T>() =>
        ValueTask.FromResult(FilePanelResult<T>.Failure(
            FilePanelMutationErrors.Unsupported));
}
