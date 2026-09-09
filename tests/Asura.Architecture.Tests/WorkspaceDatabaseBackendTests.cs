using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class WorkspaceDatabaseBackendTests
{
    [Fact]
    public async Task MissingDescriptorFailsBeforeDownload()
    {
        using var fixture = new Fixture();
        using var handler = new PayloadHandler(fixture.Payload);
        await Assert.ThrowsAsync<IOException>(() => fixture.EnsureAsync(handler));
        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(fixture.Cache));
    }

    [Fact]
    public async Task VerifiedDownloadIsCachedAndNotRequestedAgain()
    {
        using var fixture = new Fixture();
        await fixture.WriteDescriptorAsync();
        using var handler = new PayloadHandler(fixture.Payload);
        var first = await fixture.EnsureAsync(handler);
        var second = await fixture.EnsureAsync(handler);
        Assert.Equal(first, second);
        Assert.Equal(fixture.Hash, first.Hash);
        Assert.Equal(fixture.Payload, await File.ReadAllBytesAsync(first.Path, CancellationToken.None));
        Assert.Equal(1, handler.Requests);
        Assert.Empty(Directory.EnumerateFiles(fixture.Cache, "*.partial"));
    }

    [Fact]
    public async Task CorruptCachedArchiveIsReplacedOnlyWithVerifiedPayload()
    {
        using var fixture = new Fixture();
        await fixture.WriteDescriptorAsync();
        using var handler = new PayloadHandler(fixture.Payload);
        var archive = await fixture.EnsureAsync(handler);
        await File.WriteAllBytesAsync(archive.Path, new byte[fixture.Payload.Length], CancellationToken.None);
        var repaired = await fixture.EnsureAsync(handler);
        Assert.Equal(archive, repaired);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(fixture.Payload, await File.ReadAllBytesAsync(repaired.Path, CancellationToken.None));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("wrong-hash")]
    public async Task InvalidDownloadsLeaveNoCacheOrPartialFile(string kind)
    {
        using var fixture = new Fixture();
        await fixture.WriteDescriptorAsync();
        var bytes = kind switch
        {
            "truncated" => fixture.Payload[..^1],
            "oversized" => [.. fixture.Payload, 1],
            _ => new byte[fixture.Payload.Length],
        };
        using var handler = new PayloadHandler(bytes);
        await Assert.ThrowsAsync<IOException>(() => fixture.EnsureAsync(handler));
        Assert.Empty(Directory.EnumerateFiles(fixture.Cache));
    }

    [Fact]
    public async Task ConcurrentVerifiedDownloadsConvergeOnSameCacheFile()
    {
        using var fixture = new Fixture();
        await fixture.WriteDescriptorAsync();
        using var handler = new PayloadHandler(fixture.Payload, simultaneousRequests: 2);
        var results = await Task.WhenAll(fixture.EnsureAsync(handler), fixture.EnsureAsync(handler));
        Assert.Equal(results[0], results[1]);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(fixture.Payload, await File.ReadAllBytesAsync(results[0].Path, CancellationToken.None));
        Assert.Single(Directory.EnumerateFiles(fixture.Cache));
    }

    [Theory]
    [InlineData("short", 32)]
    [InlineData("uppercase", 32)]
    [InlineData("valid", 0)]
    [InlineData("valid", -1)]
    [InlineData("valid", 536870913)]
    public async Task InvalidDescriptorFailsBeforeDownload(string hashKind, long size)
    {
        using var fixture = new Fixture();
        var hash = hashKind switch { "short" => "bad", "uppercase" => fixture.Hash.ToUpperInvariant(), _ => fixture.Hash };
        await fixture.WriteDescriptorAsync(hash, size);
        using var handler = new PayloadHandler(fixture.Payload);
        await Assert.ThrowsAsync<IOException>(() => fixture.EnsureAsync(handler));
        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(fixture.Cache));
    }

    [Fact]
    public async Task LocalArchiveRequiresSamePinnedIntegrityWithoutHttp()
    {
        using var fixture = new Fixture();
        await fixture.WriteDescriptorAsync();
        var local = Path.Combine(fixture.DirectoryPath, "local.tar.gz");
        await File.WriteAllBytesAsync(local, fixture.Payload, CancellationToken.None);
        using var handler = new PayloadHandler([]);
        var archive = await WorkspaceDatabaseBackend.EnsureArchiveAsync(fixture.Descriptor, fixture.Cache,
            new Uri("https://backend.invalid/payload"), local, CancellationToken.None, handler);
        Assert.Equal(fixture.Hash, archive.Hash);
        Assert.Equal(0, handler.Requests);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("asura-backend-download-test-");
        public string DirectoryPath => _directory.FullName;
        public string Descriptor => Path.Combine(DirectoryPath, "backend-assets.json");
        public string Cache => Path.Combine(DirectoryPath, "cache");
        public byte[] Payload { get; } = "test payload pinned by the packaged descriptor"u8.ToArray();
        public string Hash => Convert.ToHexStringLower(SHA256.HashData(Payload));

        public Task WriteDescriptorAsync(string? hash = null, long? size = null) => File.WriteAllTextAsync(Descriptor,
            JsonSerializer.Serialize(new { sha256 = hash ?? Hash, size = size ?? Payload.Length }), CancellationToken.None);

        public Task<(string Path, string Hash)> EnsureAsync(HttpMessageHandler handler) =>
            WorkspaceDatabaseBackend.EnsureArchiveAsync(Descriptor, Cache, new Uri("https://backend.invalid/payload"),
                null, CancellationToken.None, handler);

        public void Dispose() => _directory.Delete(recursive: true);
    }

    private sealed class PayloadHandler(byte[] payload, int simultaneousRequests = 1) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) >= simultaneousRequests) { _ready.TrySetResult(); }
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }
    }
}
