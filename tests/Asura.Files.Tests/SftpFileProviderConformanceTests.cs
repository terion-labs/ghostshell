namespace Asura.Files.Tests;

public sealed class SftpFileProviderConformanceTests : FileProviderConformanceSuite
{
    protected override ValueTask<FileProviderTestContext> CreateContextAsync()
    {
        var options = RemoteProviderTestProfiles.SftpOptions();
        var provider = new SftpFileProvider(new FakeRemoteSessionFactory(), options);
        var root = new FileLocation(provider.ProfileId, provider.Authority, FilePath.Root);
        return ValueTask.FromResult(new FileProviderTestContext(provider, root));
    }
}
