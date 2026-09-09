namespace Asura.Files.Tests;

public sealed class S3FileProviderConformanceTests : FileProviderConformanceSuite
{
    protected override ValueTask<FileProviderTestContext> CreateContextAsync()
    {
        var profileId = new FileProviderProfileId("s3-conformance");
        var authority = new FileAuthority("test-bucket");
        var store = new FakeS3ObjectStore();
        var provider = new S3FileProvider(
            store,
            new S3FileProviderOptions(profileId, authority, "test-bucket"));
        return ValueTask.FromResult(new FileProviderTestContext(
            provider,
            new FileLocation(profileId, authority, FilePath.Root),
            assertServerSideCopyObserved: () =>
            {
                Assert.Equal(1, store.CopyCalls);
                Assert.Equal(0, store.ReadCalls);
                return ValueTask.CompletedTask;
            }));
    }
}
