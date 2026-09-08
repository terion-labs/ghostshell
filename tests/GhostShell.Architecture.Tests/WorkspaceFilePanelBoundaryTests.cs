using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Desktop;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceFilePanelBoundaryTests
{
    [Fact]
    public async Task RestoredHiddenHostHomeCannotReachTheHostCatalog()
    {
        var workspace = new RejectingClient();
        var host = new RejectingClient();
        var files = new WorkspaceFilePanelClient(workspace, host);
        var hiddenHome = new FilePanelLocation(BuiltInFileProviders.HomeId.Value, "local",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));

        _ = await files.StatAsync(hiddenHome, CancellationToken.None);

        Assert.Equal(1, workspace.Calls);
        Assert.Equal(0, host.Calls);
        Assert.DoesNotContain(files.Profiles, profile => profile.Id == BuiltInFileProviders.HomeId.Value);
    }

    private sealed class RejectingClient : IFilePanelClient
    {
        public int Calls { get; private set; }
        public IReadOnlyList<FileProviderProfileDescriptor> Profiles => [];
        public ValueTask<FilePanelResult<FilePanelEntry>> StatAsync(FilePanelLocation location, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(FilePanelResult<FilePanelEntry>.Failure(FilePanelMutationErrors.Unsupported));
        }
        public ValueTask<FilePanelResult<FilePanelPage>> ListAsync(FilePanelListRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<FilePanelResult<FilePanelPreview>> PreviewAsync(FilePanelPreviewRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<FilePanelResult<FilePanelEntry>> CreateDirectoryAsync(FilePanelCreateDirectoryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<FilePanelResult<FilePanelEntry>> RenameAsync(FilePanelRenameRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<FilePanelResult<FilePanelDeleteReceipt>> DeleteAsync(FilePanelDeleteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
