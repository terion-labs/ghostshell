using Asura.App.ViewModels;
using Asura.Application;

namespace Asura.App.Tests;

public sealed class FileTransferEditorViewModelTests
{
    [Fact]
    public void EditorBuildsStructuredCrossProviderMoveWithConflictPolicy()
    {
        var local = HierarchicalProfile("local", "Home");
        var bucket = ObjectProfile("s3-production", "Production bucket", "prod-bucket");
        var source = new FilePanelEntry(
            local.Root.Child(new FilePanelPathSegment("artifact.zip")),
            "artifact.zip",
            FilePanelEntryKind.File,
            2048,
            null,
            false);
        var editor = new FileTransferEditorViewModel(source, [local, bucket], local.Id)
        {
            SelectedDestinationProfile = bucket,
            Destination = "releases/2026/artifact.zip",
            Operation = FilePanelTransferOperation.Move,
            ConflictPolicy = FilePanelConflictPolicy.KeepBoth,
        };

        var request = editor.CreateRequest();

        Assert.Equal(source.Location, request.Source);
        Assert.Equal("s3-production", request.Destination.ProviderProfileId);
        Assert.Equal("prod-bucket", request.Destination.Authority);
        var key = Assert.IsType<FilePanelAddress.ObjectKey>(request.Destination.Address);
        Assert.Equal("releases/2026/artifact.zip", key.Key);
        Assert.Equal(FilePanelTransferOperation.Move, request.Operation);
        Assert.Equal(FilePanelConflictPolicy.KeepBoth, request.ConflictPolicy);
    }

    [Fact]
    public void SourceCannotAlsoBeTheEffectiveDestination()
    {
        var profile = HierarchicalProfile("local", "Home");
        var source = new FilePanelEntry(
            profile.Root.Child(new FilePanelPathSegment("same.txt")),
            "same.txt",
            FilePanelEntryKind.File,
            1,
            null,
            false);
        var editor = new FileTransferEditorViewModel(source, [profile])
        {
            Destination = "/same.txt",
        };

        var error = Assert.Throws<ArgumentException>(() => editor.CreateRequest());

        Assert.Contains("different", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FileProviderProfileDescriptor HierarchicalProfile(string id, string name)
    {
        var root = new FilePanelLocation(
            id,
            "local",
            new FilePanelAddress.Hierarchical(FilePanelPath.Root));
        return new FileProviderProfileDescriptor(
            id,
            name,
            FileProviderFamily.Posix,
            root,
            FilePanelCapability.List | FilePanelCapability.RangedRead,
            100,
            1024 * 1024);
    }

    private static FileProviderProfileDescriptor ObjectProfile(
        string id,
        string name,
        string authority)
    {
        var root = new FilePanelLocation(
            id,
            authority,
            new FilePanelAddress.ContainerRoot());
        return new FileProviderProfileDescriptor(
            id,
            name,
            FileProviderFamily.S3,
            root,
            FilePanelCapability.List | FilePanelCapability.RangedRead,
            100,
            1024 * 1024);
    }
}
