using Asura.Application;

namespace Asura.Application.Tests;

public sealed class FilePanelContractsTests
{
    [Fact]
    public void Location_and_profile_formatting_are_bounded_and_human_readable()
    {
        var location = new FilePanelLocation(
            "files.home",
            "local",
            new FilePanelAddress.Hierarchical(FilePanelPath.FromSegments(
                [new FilePanelPathSegment("projects"), new FilePanelPathSegment("asura")])),
            "v1");
        var profile = new FileProviderProfileDescriptor(
            "files.home",
            "Home",
            FileProviderFamily.Posix,
            location.WithVersion(null),
            FilePanelCapability.List,
            100,
            1024);

        Assert.Equal("files.home:local:/projects/asura@v1", location.ToString());
        Assert.Equal("Home", profile.ToString());
    }

    [Fact]
    public void HierarchicalLocationsUseStructuredValueEqualityAndRejectTraversal()
    {
        var first = new FilePanelLocation(
            "profile-one",
            "authority",
            new FilePanelAddress.Hierarchical(FilePanelPath.FromSegments(
            [
                new FilePanelPathSegment("folder"),
                new FilePanelPathSegment("file.txt"),
            ])));
        var second = new FilePanelLocation(
            "profile-one",
            "authority",
            new FilePanelAddress.Hierarchical(
                FilePanelPath.Root
                    .Append(new FilePanelPathSegment("folder"))
                    .Append(new FilePanelPathSegment("file.txt"))));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        var restored = second.Parent.Child(new FilePanelPathSegment("file.txt"));
        var hierarchical = Assert.IsType<FilePanelAddress.Hierarchical>(restored.Address);
        Assert.Equal("file.txt", hierarchical.Path.Name?.Value);
        Assert.Throws<ArgumentException>(() => new FilePanelPathSegment(".."));
        Assert.Throws<ArgumentException>(() => new FilePanelPathSegment("folder/file"));
    }

    [Fact]
    public void ObjectKeysRemainExactAndSeparateFromHierarchicalPaths()
    {
        var location = new FilePanelLocation(
            "s3-production",
            "bucket",
            new FilePanelAddress.ObjectKey("prefix/../literal//key"),
            "version-7");

        var key = Assert.IsType<FilePanelAddress.ObjectKey>(location.Address);

        Assert.Equal("prefix/../literal//key", key.Key);
        Assert.Equal("version-7", location.Version);
        Assert.Throws<InvalidOperationException>(() => location.Child(new FilePanelPathSegment("child")));
    }

    [Fact]
    public void PreviewCopiesCallerBufferBeforeExposingReadOnlyContent()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var preview = new FilePanelPreview(
            Root(),
            FilePanelPreviewKind.Hex,
            "application/octet-stream",
            bytes,
            false);

        bytes[0] = 99;

        Assert.Equal(new byte[] { 1, 2, 3 }, preview.Content.ToArray());
    }

    [Fact]
    public void TransferRequestRequiresValidPolicies()
    {
        var source = Root().Child(new FilePanelPathSegment("source"));
        var destination = Root().Child(new FilePanelPathSegment("destination"));

        Assert.Throws<ArgumentOutOfRangeException>(() => new FilePanelTransferRequest(
            source,
            destination,
            (FilePanelTransferOperation)999,
            FilePanelConflictPolicy.Fail));
    }

    [Fact]
    public void PanelRequestsRejectInvalidBoundsBeforeCallingAProvider()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FilePanelListRequest(
            Root(),
            PageSize: 0,
            ContinuationToken: null,
            ShowHidden: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FilePanelPreviewRequest(
            Root(),
            MaximumBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FilePanelEntry(
            Root(),
            "file.bin",
            FilePanelEntryKind.File,
            Size: -1,
            LastModifiedAt: null,
            IsHidden: false));
    }

    private static FilePanelLocation Root() => new(
        "profile-one",
        "authority",
        new FilePanelAddress.Hierarchical(FilePanelPath.Root));
}
