namespace Asura.Files.Tests;

public sealed class LocalFileProviderConformanceTests : FileProviderConformanceSuite
{
    protected override ValueTask<FileProviderTestContext> CreateContextAsync()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"asura-files-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        var profileId = new FileProviderProfileId("local-test");
        var authority = new FileAuthority("fixture");
        var provider = LocalFileProvider.CreateForCurrentPlatform(
            new LocalFileProviderOptions(profileId, authority, rootPath));
        var root = new FileLocation(profileId, authority, FilePath.Root);
        return ValueTask.FromResult(new FileProviderTestContext(
            provider,
            root,
            () =>
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }

                return ValueTask.CompletedTask;
            }));
    }
}
