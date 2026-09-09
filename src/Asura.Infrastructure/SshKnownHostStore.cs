using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Persists one OpenSSH-compatible host-key file per connection. Public keys are not secrets, but
/// atomic replacement and owner-only permissions protect the integrity of the trust decision.
/// </summary>
public sealed class SshKnownHostStore : ISshHostKeyTrustStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SharedLocks = new(StringComparer.Ordinal);
    private readonly string _directory;

    public SshKnownHostStore(SqliteStorageOptions storageOptions)
        : this(Path.Combine(
            Path.GetDirectoryName((storageOptions ?? throw new ArgumentNullException(nameof(storageOptions))).DatabasePath)!,
            "ssh-known-hosts"))
    {
    }

    public SshKnownHostStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public SshHostKeyVerification Verify(
        ConnectionId connectionId,
        SshHostKeyPolicy policy,
        SshHostKeyCandidate presented)
    {
        ArgumentNullException.ThrowIfNull(presented);
        if (policy == SshHostKeyPolicy.InsecureIgnore)
        {
            return SshHostKeyVerification.Trusted;
        }

        var identity = FileIdentity(connectionId);
        var gate = Gate(identity);
        gate.Wait();
        try
        {
            SshHostKeyCandidate? current;
            try
            {
                current = ReadWithoutLock(identity);
            }
            catch (Exception exception) when (IsStoreFailure(exception))
            {
                return SshHostKeyVerification.StoreInvalid;
            }

            if (current is not null)
            {
                return current == presented
                    ? SshHostKeyVerification.Trusted
                    : SshHostKeyVerification.Changed;
            }

            if (policy != SshHostKeyPolicy.AcceptNew)
            {
                return SshHostKeyVerification.Unknown;
            }

            try
            {
                if (TryWriteNewWithoutLock(identity, presented))
                {
                    return SshHostKeyVerification.Trusted;
                }

                current = ReadAfterConcurrentCreate(identity);
                return current == presented
                    ? SshHostKeyVerification.Trusted
                    : SshHostKeyVerification.Changed;
            }
            catch (Exception exception) when (IsStoreFailure(exception))
            {
                return SshHostKeyVerification.StoreInvalid;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<SshHostKeyCandidate?> ReadAsync(
        ConnectionId connectionId,
        CancellationToken cancellationToken)
    {
        var identity = FileIdentity(connectionId);
        var gate = Gate(identity);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadWithoutLockAsync(identity, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async ValueTask<SshKnownHostWriteResult> WriteAsync(
        ConnectionId connectionId,
        SshHostKeyCandidate candidate,
        SshHostKeyCandidate? expectedCurrent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var identity = FileIdentity(connectionId);
        var gate = Gate(identity);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadWithoutLockAsync(identity, cancellationToken).ConfigureAwait(false);
            if (current == candidate)
            {
                return SshKnownHostWriteResult.AlreadyCurrent;
            }

            if (current != expectedCurrent)
            {
                return SshKnownHostWriteResult.ChangedSinceReview;
            }

            if (current is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryWriteNewWithoutLock(identity, candidate))
                {
                    return SshKnownHostWriteResult.Stored;
                }

                current = await ReadAfterConcurrentCreateAsync(identity, cancellationToken)
                    .ConfigureAwait(false);
                return current == candidate
                    ? SshKnownHostWriteResult.AlreadyCurrent
                    : SshKnownHostWriteResult.ChangedSinceReview;
            }

            await WriteWithoutLockAsync(identity, candidate, cancellationToken).ConfigureAwait(false);
            return SshKnownHostWriteResult.Stored;
        }
        finally
        {
            gate.Release();
        }
    }

    internal SshKnownHostBinding Binding(ConnectionId connectionId)
    {
        var identity = FileIdentity(connectionId);
        return new SshKnownHostBinding(
            Path.Combine(_directory, $"{identity}.known_hosts"),
            $"asura-{identity}");
    }

    private async ValueTask<SshHostKeyCandidate?> ReadWithoutLockAsync(
        string identity,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, $"{identity}.known_hosts");
        if (!File.Exists(path))
        {
            return null;
        }

        RejectOversizedFile(path);
        var line = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return Parse(identity, line);
    }

    private async ValueTask WriteWithoutLockAsync(
        string identity,
        SshHostKeyCandidate candidate,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        RestrictDirectoryPermissions(_directory);
        var target = Path.Combine(_directory, $"{identity}.known_hosts");
        var temporary = Path.Combine(_directory, $".{identity}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                    temporary,
                    Format(identity, candidate),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            RestrictFilePermissions(temporary);
            File.Move(temporary, target, overwrite: true);
            RestrictFilePermissions(target);
        }
        finally
        {
            TryDeleteTemporary(temporary);
        }
    }

    private SshHostKeyCandidate? ReadWithoutLock(string identity)
    {
        var path = Path.Combine(_directory, $"{identity}.known_hosts");
        if (!File.Exists(path))
        {
            return null;
        }

        RejectOversizedFile(path);
        return Parse(identity, File.ReadAllText(path));
    }

    private bool TryWriteNewWithoutLock(string identity, SshHostKeyCandidate candidate)
    {
        Directory.CreateDirectory(_directory);
        RestrictDirectoryPermissions(_directory);
        var target = Path.Combine(_directory, $"{identity}.known_hosts");
        var created = false;
        var complete = false;
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var stream = new FileStream(target, options);
            created = true;
            RestrictFilePermissions(target);
            var line = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(
                Format(identity, candidate));
            try
            {
                stream.Write(line);
                stream.Flush(flushToDisk: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(line);
            }

            complete = true;
            return true;
        }
        catch (IOException) when (!created && File.Exists(target))
        {
            return false;
        }
        finally
        {
            if (created && !complete)
            {
                TryDeleteTemporary(target);
            }
        }
    }

    private SshHostKeyCandidate? ReadAfterConcurrentCreate(string identity)
    {
        const int attempts = 20;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return ReadWithoutLock(identity);
            }
            catch (Exception exception)
                when (attempt + 1 < attempts
                    && exception is IOException or InvalidDataException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(5));
            }
        }
    }

    private async ValueTask<SshHostKeyCandidate?> ReadAfterConcurrentCreateAsync(
        string identity,
        CancellationToken cancellationToken)
    {
        const int attempts = 20;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ReadWithoutLockAsync(identity, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
                when (attempt + 1 < attempts
                    && exception is IOException or InvalidDataException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static SshHostKeyCandidate Parse(string identity, string line)
    {
        if (line.Length > 128 * 1024)
        {
            throw new InvalidDataException("The trusted SSH host-key file is malformed.");
        }

        var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || !string.Equals(fields[0], $"asura-{identity}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The trusted SSH host-key file is malformed.");
        }

        try
        {
            return new SshHostKeyCandidate(fields[1], fields[2]);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The trusted SSH host-key file is malformed.", exception);
        }
    }

    private static void RejectOversizedFile(string path)
    {
        if (new FileInfo(path).Length > 128 * 1024)
        {
            throw new InvalidDataException("The trusted SSH host-key file is malformed.");
        }
    }

    private static string Format(string identity, SshHostKeyCandidate candidate) =>
        $"asura-{identity} {candidate.Identity.Algorithm} {candidate.PublicKeyBase64}\n";

    private static void TryDeleteTemporary(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (IOException)
        {
            // The atomic move already committed or another cleanup owns the temporary path.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the classified write outcome if the platform denies best-effort cleanup.
        }
    }

    private static bool IsStoreFailure(Exception exception) => exception is
        IOException or
        InvalidDataException or
        UnauthorizedAccessException;

    private static string FileIdentity(ConnectionId connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId.Value);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(connectionId.Value));
        return Convert.ToHexStringLower(digest);
    }

    private SemaphoreSlim Gate(string identity) => SharedLocks.GetOrAdd(
        $"{_directory}\n{identity}",
        static _ => new SemaphoreSlim(1, 1));

    private static void RestrictDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new UnauthorizedAccessException("The current Windows user has no security identifier.");
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
        }
        else
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new UnauthorizedAccessException("The current Windows user has no security identifier.");
            var permissions = new FileSecurity();
            permissions.SetOwner(owner);
            permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            permissions.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(permissions);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
