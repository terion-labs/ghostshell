namespace Asura.Files.Tests;

public sealed class FileLocationTests
{
    [Fact]
    public void ObjectKeysRemainOpaqueAndContainerRootIsDistinct()
    {
        var profileId = new FileProviderProfileId("object-provider");
        var authority = new FileAuthority("bucket");
        var exactKey = new FileObjectKey("folder//./../name/");
        var objectLocation = FileLocation.ForObjectKey(profileId, authority, exactKey);
        var containerRoot = FileLocation.ForContainerRoot(profileId, authority);

        Assert.Equal(exactKey, objectLocation.ObjectKey);
        Assert.False(objectLocation.IsContainerRoot);
        Assert.IsType<FileLocationAddress.Object>(objectLocation.Address);
        Assert.Throws<InvalidOperationException>(() => objectLocation.Path);
        Assert.Throws<InvalidOperationException>(() =>
            objectLocation.Child(new FilePathSegment("child")));

        Assert.True(containerRoot.IsContainerRoot);
        Assert.Null(containerRoot.ObjectKey);
        Assert.NotEqual(objectLocation, containerRoot);
    }

    [Fact]
    public void ObjectKeyCannotBeEmptyBecauseContainerRootHasItsOwnAddressKind()
    {
        Assert.Throws<ArgumentException>(() => new FileObjectKey(string.Empty));
    }
}
