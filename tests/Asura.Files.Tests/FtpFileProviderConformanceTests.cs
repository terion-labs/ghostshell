namespace Asura.Files.Tests;

public sealed class FtpFileProviderConformanceTests : FileProviderConformanceSuite
{
    protected override ValueTask<FileProviderTestContext> CreateContextAsync()
    {
        var options = RemoteProviderTestProfiles.FtpOptions();
        var provider = new FtpFileProvider(new FakeRemoteSessionFactory(), options);
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);
        return ValueTask.FromResult(new FileProviderTestContext(provider, root));
    }
}
