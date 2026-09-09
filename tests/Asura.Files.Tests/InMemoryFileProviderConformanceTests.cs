namespace Asura.Files.Tests;

public sealed class InMemoryFileProviderConformanceTests : FileProviderConformanceSuite
{
    protected override ValueTask<FileProviderTestContext> CreateContextAsync()
    {
        var profileId = new FileProviderProfileId("memory-test");
        var authority = new FileAuthority("fixture");
        var provider = new InMemoryFileProvider(profileId, authority);
        var root = new FileLocation(profileId, authority, FilePath.Root);
        return ValueTask.FromResult(new FileProviderTestContext(provider, root));
    }
}
