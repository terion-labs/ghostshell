namespace Asura.Files.Tests;

public sealed class SmbFileProviderConformanceTests : FileProviderConformanceSuite
{
    protected override ValueTask<FileProviderTestContext> CreateContextAsync()
    {
        var options = RemoteProviderTestProfiles.SmbOptions();
        var provider = new SmbFileProvider(new FakeRemoteSessionFactory(), options);
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);
        return ValueTask.FromResult(new FileProviderTestContext(provider, root));
    }
}
