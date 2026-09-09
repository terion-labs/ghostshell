using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asura.Infrastructure.Tests;

public sealed class WorkspaceBootAssetsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"asura-boot-{Guid.NewGuid():N}");
    private readonly byte[] _kernel = Encoding.UTF8.GetBytes("test kernel");
    private readonly byte[] _initfs = Encoding.UTF8.GetBytes("test boot filesystem");
    private readonly byte[] _archive;

    public WorkspaceBootAssetsTests()
    {
        Directory.CreateDirectory(_directory);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var entry = zip.CreateEntry("kernel.bin").Open())
            {
                entry.Write(_kernel);
            }
            using var initfs = zip.CreateEntry("initfs.ext4").Open();
            initfs.Write(_initfs);
        }
        _archive = output.ToArray();
        WriteDescriptor(_archive, _kernel, _initfs);
    }

    [Fact]
    public async Task First_use_downloads_once_and_verified_cache_works_offline()
    {
        using var online = new Download(_archive);
        var path = await Assets().EnsureAsync(null, CancellationToken.None, online);
        Assert.Equal(_kernel, await File.ReadAllBytesAsync(Path.Combine(path, "kernel.bin"), CancellationToken.None));
        Assert.Equal(_initfs, await File.ReadAllBytesAsync(Path.Combine(path, "initfs.ext4"), CancellationToken.None));
        Assert.Equal(1, online.Calls);
        using var offline = new Download(null);
        Assert.Equal(path, await Assets().EnsureAsync(null, CancellationToken.None, offline));
        Assert.Equal(0, offline.Calls);
        Assert.Empty(Directory.GetFiles(path, "*.partial"));
    }

    [Fact]
    public async Task Concurrent_workspaces_share_one_download()
    {
        using var download = new Download(_archive);
        var paths = await Task.WhenAll(Assets().EnsureAsync(null, CancellationToken.None, download),
            Assets().EnsureAsync(null, CancellationToken.None, download));
        Assert.Equal(paths[0], paths[1]);
        Assert.Equal(1, download.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Truncated_or_corrupt_download_is_not_installed_and_retry_succeeds(bool truncate)
    {
        var badBytes = _archive.ToArray();
        badBytes[^1] ^= 1;
        using var bad = new Download(truncate ? badBytes[..^1] : badBytes);
        await Assert.ThrowsAsync<IOException>(() => Assets().EnsureAsync(null, CancellationToken.None, bad));
        Assert.Empty(Directory.GetFiles(_directory, "kernel.bin", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial", SearchOption.AllDirectories));
        using var good = new Download(_archive);
        _ = await Assets().EnsureAsync(null, CancellationToken.None, good);
        Assert.Equal(1, good.Calls);
    }

    [Fact]
    public async Task Corrupted_cache_is_repaired_instead_of_booted()
    {
        using var download = new Download(_archive);
        var path = await Assets().EnsureAsync(null, CancellationToken.None, download);
        await File.WriteAllBytesAsync(Path.Combine(path, "kernel.bin"), new byte[_kernel.Length], CancellationToken.None);
        using var offline = new Download(null);
        var error = await Assert.ThrowsAsync<IOException>(() => Assets().EnsureAsync(null, CancellationToken.None, offline));
        Assert.Contains("Connect to the internet and retry", error.Message, StringComparison.Ordinal);
        _ = await Assets().EnsureAsync(null, CancellationToken.None, download);
        Assert.Equal(2, download.Calls);
        Assert.Equal(_kernel, await File.ReadAllBytesAsync(Path.Combine(path, "kernel.bin"), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_cleans_partial_download_and_releases_lock()
    {
        using var cancellation = new CancellationTokenSource();
        using var download = new Download(_archive, cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Assets().EnsureAsync(null, cancellation.Token, download));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial", SearchOption.AllDirectories));
        using var retry = new Download(_archive);
        _ = await Assets().EnsureAsync(null, CancellationToken.None, retry);
    }

    [Fact]
    public async Task Image_checksum_is_checked_independently_of_archive_checksum()
    {
        WriteDescriptor(_archive, Encoding.UTF8.GetBytes("bad kernel!"), _initfs);
        using var download = new Download(_archive);
        await Assert.ThrowsAsync<IOException>(() => Assets().EnsureAsync(null, CancellationToken.None, download));
        Assert.Empty(Directory.GetFiles(_directory, "kernel.bin", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Development_sidecar_uses_identical_validation_without_network()
    {
        var local = Path.Combine(_directory, "local.zip");
        await File.WriteAllBytesAsync(local, _archive, CancellationToken.None);
        using var offline = new Download(null);
        _ = await Assets().EnsureAsync(null, CancellationToken.None, offline, local);
        Assert.Equal(0, offline.Calls);
    }

    private WorkspaceBootAssets Assets() => new(Path.Combine(_directory, "boot-assets.json"),
        Path.Combine(_directory, "cache"), new Uri("https://example.invalid/boot.zip"));

    private void WriteDescriptor(byte[] archive, byte[] kernel, byte[] initfs)
    {
        static object Pin(byte[] bytes) => new { sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), size = bytes.Length };
        File.WriteAllText(Path.Combine(_directory, "boot-assets.json"), JsonSerializer.Serialize(new
        {
            sha256 = Convert.ToHexStringLower(SHA256.HashData(archive)),
            size = archive.Length,
            files = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["kernel.bin"] = Pin(kernel),
                ["initfs.ext4"] = Pin(initfs),
            },
        }));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class Download(byte[]? bytes, CancellationTokenSource? cancellation = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellation?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is null)
            {
                throw new HttpRequestException("Offline fixture");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
