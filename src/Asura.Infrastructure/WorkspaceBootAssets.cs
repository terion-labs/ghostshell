using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>
/// Provisions immutable boot images on first isolation use. Only the descriptor
/// ships in the signed app; downloaded bytes must match its hashes before boot.
/// This host bootstrap download precedes the workspace and carries no guest traffic.
/// </summary>
internal sealed class WorkspaceBootAssets(string descriptorPath, string cacheRoot, Uri downloadUri)
{
    internal const string ArchiveName = "Asura-workspace-boot-arm64.zip";
    private static readonly string[] Names = ["kernel.bin", "initfs.ext4"];

    internal async Task<string> EnsureAsync(
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null,
        string? localArchive = null)
    {
        try
        {
            using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(descriptorPath, cancellationToken).ConfigureAwait(false));
            var archivePin = ReadPin(descriptor.RootElement, 256L * 1024 * 1024);
            var pins = Names.ToDictionary(name => name,
                name => ReadPin(descriptor.RootElement.GetProperty("files").GetProperty(name), 1024L * 1024 * 1024), StringComparer.Ordinal);
            var directory = Path.Combine(cacheRoot, archivePin.Hash);
            CreatePrivateDirectory(directory);
            // A filesystem lock also serializes independent windows/processes. No
            // completion marker can outlive a corrupt or partially written image.
            await using var lease = await LockAsync(Path.Combine(directory, "provision.lock"), cancellationToken).ConfigureAwait(false);
            progress?.Report(new WorkspaceIsolationProgress("Verifying cached workspace boot images…"));
            if (await ValidImagesAsync(directory, pins, cancellationToken).ConfigureAwait(false))
            {
                return directory;
            }

            var pending = Path.Combine(directory, "download.partial");
            try
            {
                if (localArchive is not null)
                {
                    await using var source = File.OpenRead(localArchive);
                    await CopyAsync(source, pending, archivePin.Size, progress, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromMinutes(15));
                    progress?.Report(new WorkspaceIsolationProgress("Downloading workspace boot images for first use…"));
                    using var response = await client.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    await CopyAsync(source, pending, archivePin.Size, progress, timeout.Token).ConfigureAwait(false);
                }

                if (!await ValidAsync(pending, archivePin, cancellationToken).ConfigureAwait(false))
                {
                    throw new IOException("Workspace boot download failed its integrity check. Retry to download a fresh copy.");
                }

                progress?.Report(new WorkspaceIsolationProgress("Installing verified workspace boot images…"));
                using var archive = ZipFile.OpenRead(pending);
                if (archive.Entries.Count != Names.Length)
                {
                    throw new IOException("The workspace boot archive has unexpected contents.");
                }
                foreach (var name in Names)
                {
                    var entry = archive.GetEntry(name) ?? throw new IOException("The workspace boot archive is incomplete.");
                    var path = Path.Combine(directory, name);
                    var temporary = path + ".partial";
                    try
                    {
                        await using var source = entry.Open();
                        await CopyAsync(source, temporary, pins[name].Size, null, cancellationToken).ConfigureAwait(false);
                        if (!await ValidAsync(temporary, pins[name], cancellationToken).ConfigureAwait(false))
                        {
                            throw new IOException("A workspace boot image failed its integrity check.");
                        }
                        File.Move(temporary, path, overwrite: true);
                    }
                    finally
                    {
                        File.Delete(temporary);
                    }
                }
                return directory;
            }
            finally
            {
                File.Delete(pending);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Workspace boot images could not be downloaded. Connect to the internet and retry; completed cached images work offline.", exception);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new IOException("The app's workspace boot descriptor is invalid. Reinstall the app.", exception);
        }
    }

    private static (string Hash, long Size) ReadPin(JsonElement element, long maximum)
    {
        var hash = element.GetProperty("sha256").GetString();
        var size = element.GetProperty("size").GetInt64();
        if (hash is not { Length: 64 } || hash.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0 || size <= 0 || size > maximum)
        {
            throw new IOException("The app's workspace boot descriptor has an invalid checksum or size.");
        }
        return (hash, size);
    }

    private static async Task<bool> ValidImagesAsync(string directory,
        Dictionary<string, (string Hash, long Size)> pins, CancellationToken cancellationToken)
    {
        foreach (var name in Names)
        {
            if (!await ValidAsync(Path.Combine(directory, name), pins[name], cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<bool> ValidAsync(string path, (string Hash, long Size) pin, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != pin.Size)
        {
            return false;
        }
        await using var stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)),
            pin.Hash, StringComparison.Ordinal);
    }

    private static async Task CopyAsync(Stream source, string path, long size,
        IProgress<WorkspaceIsolationProgress>? progress, CancellationToken cancellationToken)
    {
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var buffer = new byte[128 * 1024];
        long total = 0;
        var lastPercent = -1;
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > size)
            {
                throw new IOException("The workspace boot download exceeds its expected size.");
            }
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            var percent = (int)(total * 100 / size);
            if (percent != lastPercent)
            {
                progress?.Report(new WorkspaceIsolationProgress(string.Create(CultureInfo.InvariantCulture,
                    $"Downloading workspace boot images… {percent}%")));
                lastPercent = percent;
            }
        }
        if (total != size)
        {
            throw new IOException("The workspace boot download was interrupted. Retry to download a fresh copy.");
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(16));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(200, timeout.Token).ConfigureAwait(false);
            }
        }
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
