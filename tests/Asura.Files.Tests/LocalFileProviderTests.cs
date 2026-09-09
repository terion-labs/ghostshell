using System.Text;

namespace Asura.Files.Tests;

public sealed class LocalFileProviderTests
{
    [Fact]
    public void FactorySelectsHostSemanticsAndReportsCaseBehaviorHonestly()
    {
        using var root = TemporaryDirectory.Create();
        var provider = CreateProvider(root.Path);

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsLocalFileProvider>(provider);
            Assert.Equal(FileNameComparison.CaseInsensitive, provider.Capabilities.NameComparison);
        }
        else
        {
            Assert.IsType<PosixLocalFileProvider>(provider);
            var expected = OperatingSystem.IsMacOS()
                ? FileNameComparison.ProviderDefined
                : FileNameComparison.CaseSensitive;
            Assert.Equal(expected, provider.Capabilities.NameComparison);
        }
    }

    [Fact]
    public void StructuredPathsRejectTraversalAndUseValueEquality()
    {
        Assert.Throws<ArgumentException>(() => new FilePathSegment(".."));
        Assert.Throws<ArgumentException>(() => new FilePathSegment("a/b"));

        var first = FilePath.FromSegments([
            new FilePathSegment("folder"),
            new FilePathSegment("file.txt"),
        ]);
        var second = FilePath.Root
            .Append(new FilePathSegment("folder"))
            .Append(new FilePathSegment("file.txt"));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(first.IsDescendantOf(first.Parent));
    }

    [Fact]
    public async Task LinkIsVisibleAsMetadataButCannotEscapeTheRoot()
    {
        using var root = TemporaryDirectory.Create();
        using var outside = TemporaryDirectory.Create();
        var outsideFile = Path.Combine(outside.Path, "outside.txt");
        await File.WriteAllTextAsync(outsideFile, "outside");
        var linkPath = Path.Combine(root.Path, "escape");

        try
        {
            File.CreateSymbolicLink(linkPath, outsideFile);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var provider = CreateProvider(root.Path);
        var link = Root(provider).Child(new FilePathSegment("escape"));
        var stat = await provider.StatAsync(new FileStatRequest(link), CancellationToken.None);
        Assert.True(stat.IsSuccess, stat.Error?.Message);
        Assert.Equal(FileEntryKind.Link, stat.Value!.Kind);

        await using var destination = new MemoryStream();
        var read = await provider.ReadAsync(
            new FileReadRequest(link, 0, maximumBytes: 32, bufferSize: 4),
            destination,
            progress: null,
            CancellationToken.None);
        Assert.Equal(FileProviderErrorCode.LinkNotAllowed, read.Error!.Code);
        Assert.Empty(destination.ToArray());

        var nested = link.Child(new FilePathSegment("nested"));
        var nestedStat = await provider.StatAsync(new FileStatRequest(nested), CancellationToken.None);
        Assert.Equal(FileProviderErrorCode.LinkNotAllowed, nestedStat.Error!.Code);

        var deleted = await provider.DeleteAsync(
            new FileDeleteRequest(link, recursive: false, new FileMutationPrecondition.Any()),
            CancellationToken.None);
        Assert.True(deleted.IsSuccess, deleted.Error?.Message);
        Assert.True(File.Exists(outsideFile));
        Assert.False(File.Exists(linkPath));
    }

    [Fact]
    public void ProviderRootCannotBeAReparsePoint()
    {
        using var parent = TemporaryDirectory.Create();
        using var target = TemporaryDirectory.Create();
        var linkPath = Path.Combine(parent.Path, "root-link");

        try
        {
            Directory.CreateSymbolicLink(linkPath, target.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.Throws<ArgumentException>(() => CreateProvider(linkPath));
        Directory.Delete(linkPath);
    }

    [Fact]
    public async Task HostSpecificNameRulesAreEnforcedAtTheProviderBoundary()
    {
        using var root = TemporaryDirectory.Create();
        var provider = CreateProvider(root.Path);
        var name = OperatingSystem.IsWindows() ? "CON.txt" : "back\\slash.txt";
        var location = Root(provider).Child(new FilePathSegment(name));
        var bytes = Encoding.UTF8.GetBytes("content");
        await using var source = new MemoryStream(bytes, writable: false);

        var result = await provider.WriteAsync(
            new FileWriteRequest(
                location,
                bytes.Length,
                bufferSize: 4,
                new FileMutationPrecondition.MustNotExist()),
            source,
            progress: null,
            CancellationToken.None);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(FileProviderErrorCode.InvalidName, result.Error!.Code);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.True(File.Exists(Path.Combine(root.Path, name)));
        }
    }

    [Fact]
    public async Task ObjectAddressIsRejectedAsTypedInvalidLocationInsteadOfParsedAsALocalPath()
    {
        using var root = TemporaryDirectory.Create();
        var provider = CreateProvider(root.Path);
        var objectLocation = FileLocation.ForObjectKey(
            provider.ProfileId,
            provider.Authority,
            new FileObjectKey("../outside"));

        var result = await provider.StatAsync(
            new FileStatRequest(objectLocation),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(FileProviderErrorCode.InvalidLocation, result.Error!.Code);
    }

    [Fact]
    public async Task ContinuationPagesUseTheInitialDirectorySnapshot()
    {
        using var root = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(Path.Combine(root.Path, "one.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "two.txt"), "2");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "three.txt"), "3");
        var provider = CreateProvider(root.Path);
        var location = Root(provider);

        var first = await provider.ListAsync(
            new FileListRequest(location, pageSize: 2),
            CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.NotNull(first.Value!.ContinuationToken);

        await File.WriteAllTextAsync(Path.Combine(root.Path, "added-later.txt"), "4");
        var second = await provider.ListAsync(
            new FileListRequest(location, pageSize: 2, first.Value.ContinuationToken),
            CancellationToken.None);

        Assert.True(second.IsSuccess, second.Error?.Message);
        Assert.Single(second.Value!.Items);
        Assert.Null(second.Value.ContinuationToken);
    }

    private static LocalFileProvider CreateProvider(string rootPath)
    {
        var options = new LocalFileProviderOptions(
            new FileProviderProfileId("local-specific"),
            new FileAuthority("fixture"),
            rootPath);
        return LocalFileProvider.CreateForCurrentPlatform(options);
    }

    private static FileLocation Root(LocalFileProvider provider) =>
        new(provider.ProfileId, provider.Authority, FilePath.Root);
}
