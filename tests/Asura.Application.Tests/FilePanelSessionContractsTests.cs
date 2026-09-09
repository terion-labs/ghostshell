using Asura.Core;

namespace Asura.Application.Tests;

public sealed class FilePanelSessionContractsTests
{
    [Fact]
    public void FileControlPlaneOperationsAndCapabilitiesHaveStableNames()
    {
        Assert.Equal("files.open", ApplicationOperations.FilesOpen);
        Assert.Equal("files.list", ApplicationOperations.FilesList);
        Assert.Equal("files.stat", ApplicationOperations.FilesStat);
        Assert.Equal("files.preview", ApplicationOperations.FilesPreview);
        Assert.Equal("files.mkdir", ApplicationOperations.FilesCreateDirectory);
        Assert.Equal("files.rename", ApplicationOperations.FilesRename);
        Assert.Equal("files.delete", ApplicationOperations.FilesDelete);
        Assert.Equal("files.transfer.enqueue", ApplicationOperations.FilesTransferEnqueue);
        Assert.Equal("files.transfer.cancel", ApplicationOperations.FilesTransferCancel);
        Assert.Equal("files.transfer.retry", ApplicationOperations.FilesTransferRetry);

        Assert.Equal(ApplicationOperations.FilesList, SessionCapabilities.FilesList);
        Assert.Equal(ApplicationOperations.FilesStat, SessionCapabilities.FilesStat);
        Assert.Equal(ApplicationOperations.FilesPreview, SessionCapabilities.FilesPreview);
        Assert.Equal(
            ApplicationOperations.FilesCreateDirectory,
            SessionCapabilities.FilesCreateDirectory);
        Assert.Equal(ApplicationOperations.FilesRename, SessionCapabilities.FilesRename);
        Assert.Equal(ApplicationOperations.FilesDelete, SessionCapabilities.FilesDelete);
        Assert.Equal(
            ApplicationOperations.FilesTransferEnqueue,
            SessionCapabilities.FilesTransferEnqueue);
        Assert.Equal(
            ApplicationOperations.FilesTransferCancel,
            SessionCapabilities.FilesTransferCancel);
        Assert.Equal(
            ApplicationOperations.FilesTransferRetry,
            SessionCapabilities.FilesTransferRetry);
    }

    [Fact]
    public void HostRequestsKeepSessionIdentitySeparateFromProviderNeutralPayloads()
    {
        var sessionId = new SessionId("files-1");
        var root = new FilePanelLocation(
            "profile-1",
            "host",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        var owner = new SessionOwner(
            HostMode.Desktop,
            new WindowInstanceId("window-1"),
            new WorkspaceInstanceId("workspace-1"),
            new TabInstanceId("tab-1"),
            new PanelInstanceId("panel-1"));

        var ensure = new EnsureFilePanelSessionRequest(sessionId, owner, "Files", root);
        var list = new FilePanelListHostRequest(
            sessionId,
            new FilePanelListRequest(root, 25, null, ShowHidden: false));
        var transfer = new FilePanelTransferEnqueueHostRequest(
            sessionId,
            new FilePanelTransferRequest(
                root.Child(new FilePanelPathSegment("source")),
                root.Child(new FilePanelPathSegment("destination")),
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail));

        Assert.Equal(owner, ensure.Owner);
        Assert.Equal(root, ensure.InitialLocation);
        Assert.Equal(sessionId, list.SessionId);
        Assert.Equal(root, list.Request.Location);
        Assert.Equal(sessionId, transfer.SessionId);
        Assert.Equal("profile-1", transfer.Request.Source.ProviderProfileId);
    }
}
