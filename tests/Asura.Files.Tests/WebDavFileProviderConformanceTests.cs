namespace Asura.Files.Tests;

public sealed class WebDavFileProviderConformanceTests : FileProviderConformanceSuite
{
    protected override ValueTask<FileProviderTestContext> CreateContextAsync()
    {
        var handler = new FakeWebDavHandler();
        var client = new HttpClient(handler);
        var profileId = new FileProviderProfileId("webdav-conformance");
        var authority = new FileAuthority("dav.test");
        var provider = new WebDavFileProvider(
            client,
            new WebDavFileProviderOptions(
                profileId,
                authority,
                new Uri("https://dav.test/root/")));
        return ValueTask.FromResult(new FileProviderTestContext(
            provider,
            new FileLocation(profileId, authority, FilePath.Root),
            () =>
            {
                client.Dispose();
                return ValueTask.CompletedTask;
            },
            () =>
            {
                Assert.Equal(1, handler.CopyRequests);
                Assert.Equal(0, handler.GetRequests);
                return ValueTask.CompletedTask;
            }));
    }
}
