using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GhostShell.Infrastructure;

/// <summary>
/// Caches an immutable, credential-free bootstrap disk. A cross-process lock serializes
/// provisioning; atomic publication and SHA-256 verification reject partial/corrupt disks.
/// Each route receives a private APFS clone (ordinary copy on other filesystems).
/// </summary>
internal static partial class WorkspaceServiceDiskTemplate
{
    internal static async ValueTask CloneAsync(string cacheRoot, string identity, string destination,
        Func<string, CancellationToken, ValueTask> prepare, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        CreatePrivateDirectory(cacheRoot);
        var entry = Path.Combine(cacheRoot, key);
        var staging = Path.Combine(cacheRoot, $"{key}.building");
        await using var gate = await AcquireAsync(Path.Combine(cacheRoot, $"{key}.lock"), cancellationToken).ConfigureAwait(false);
        RejectSymbolicLink(entry);
        if (!Directory.Exists(entry))
        {
            CreatePrivateDirectory(staging);
            var stagedDisk = Path.Combine(staging, "rootfs.ext4");
            RejectSymbolicLink(stagedDisk);
            if (!File.Exists(stagedDisk))
            {
                await prepare(stagedDisk, cancellationToken).ConfigureAwait(false);
            }
            var digest = await DigestAsync(stagedDisk, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(staging, "rootfs.sha256"), digest, cancellationToken).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stagedDisk, UnixFileMode.UserRead);
            }
            Directory.Move(staging, entry);
        }

        var template = Path.Combine(entry, "rootfs.ext4");
        var manifest = Path.Combine(entry, "rootfs.sha256");
        RejectSymbolicLink(template);
        RejectSymbolicLink(manifest);
        if (new FileInfo(manifest).Length != 64)
        {
            throw new IOException("The cached service image checksum is invalid.");
        }
        var expected = await File.ReadAllTextAsync(manifest, cancellationToken).ConfigureAwait(false);
        if (expected.Length != 64 || expected.Any(static character => !char.IsAsciiHexDigit(character))
            || !string.Equals(expected, await DigestAsync(template, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
        {
            throw new IOException("The cached service image failed verification; it was not started.");
        }

        var pending = Path.Combine(Path.GetDirectoryName(destination)!, $"service-clone-{Guid.NewGuid():N}.ext4");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsMacOS() || CloneFile(template, pending, 0) != 0)
            {
                File.Copy(template, pending, overwrite: false);
            }
            if (!string.Equals(expected, await DigestAsync(pending, cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
            {
                throw new IOException("The private service image clone failed verification.");
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(pending, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(pending, destination, overwrite: false);
        }
        finally
        {
            // This unique unpublished clone is never a user workspace disk.
            if (File.Exists(pending)) { File.Delete(pending); }
        }
    }

    private static async ValueTask<FileStream> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(16));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            RejectSymbolicLink(path);
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> DigestAsync(string path, CancellationToken cancellationToken)
    {
        RejectSymbolicLink(path);
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
    }

    private static void CreatePrivateDirectory(string path)
    {
        RejectSymbolicLink(path);
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(path); }
        else { Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    private static void RejectSymbolicLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null)
        {
            throw new IOException("A service image cache path was replaced by a symbolic link.");
        }
    }

    [LibraryImport("libc", EntryPoint = "clonefile", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CloneFile(string source, string destination, uint flags);
}
