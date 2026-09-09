using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>
/// Keeps a private key in an owner-only, delete-on-close file only for the lifetime of one SSH
/// helper. The open handle makes normal exit and process termination recoverable by the OS.
/// </summary>
internal sealed class EphemeralPrivateKeyFile : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly string _directory;
    private bool _disposed;

    private EphemeralPrivateKeyFile(string directory, string path, FileStream stream)
    {
        _directory = directory;
        Path = path;
        _stream = stream;
    }

    public string Path { get; }

    public static async ValueTask<EphemeralPrivateKeyFile> CreateAsync(
        SecretMaterial material,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(material);
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"asura-ssh-{RandomNumberGenerator.GetHexString(16, lowercase: true)}");
        Directory.CreateDirectory(directory);
        RestrictDirectoryPermissions(directory);

        var path = System.IO.Path.Combine(directory, "identity");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.DeleteOnClose);
            RestrictFilePermissions(path);

            var buffer = new byte[material.Length];
            try
            {
                material.CopyTo(buffer);
                await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Position = 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }

            return new EphemeralPrivateKeyFile(directory, path, stream);
        }
        catch
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            TryDelete(path);
            TryDeleteDirectory(directory);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stream.Position = 0;
            var zeros = new byte[16 * 1024];
            var remaining = _stream.Length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, zeros.Length);
                await _stream.WriteAsync(zeros.AsMemory(0, count)).ConfigureAwait(false);
                remaining -= count;
            }

            await _stream.FlushAsync().ConfigureAwait(false);
            _stream.SetLength(0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Delete-on-close remains the fail-safe when best-effort overwrite is unavailable.
        }
        finally
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            TryDelete(Path);
            TryDeleteDirectory(_directory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RestrictDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new UnauthorizedAccessException(
                    "The current Windows user has no security identifier.");
            var permissions = new DirectorySecurity();
            permissions.SetOwner(owner);
            permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            permissions.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(permissions);
            return;
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new UnauthorizedAccessException(
                    "The current Windows user has no security identifier.");
            var permissions = new FileSecurity();
            permissions.SetOwner(owner);
            permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            permissions.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(permissions);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
