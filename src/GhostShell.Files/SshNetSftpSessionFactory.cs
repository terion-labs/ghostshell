using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GhostShell.Files;

/// <summary>Owns SSH.NET authentication material and host-key enforcement inside the adapter boundary.</summary>
internal sealed class SshNetSftpSessionFactory(
    ISecretVault secretVault,
    ISshHostKeyTrustStore knownHosts,
    SftpFileProviderOptions options,
    IConnectionRuntime? connectionRuntime = null,
    ISshAgentIdentitySource? agentIdentitySource = null,
    IWorkspaceNetworkConnector? networkConnector = null)
    : IRemoteHierarchicalFileSessionFactory
{
    private readonly SystemSshAuthenticationBridge _systemAuthentication = new(
        options.Connection,
        connectionRuntime,
        agentIdentitySource ?? new SystemSshAgentIdentitySource());

    public async ValueTask<IRemoteHierarchicalFileSession> OpenAsync(
        CancellationToken cancellationToken)
    {
        var endpoint = (ConnectionEndpoint.Ssh)options.Connection.Endpoint;
        if (string.IsNullOrWhiteSpace(endpoint.Username))
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.InvalidConfiguration,
                "The SSH profile requires an explicit username for SFTP.");
        }

        var ownedBuffers = new List<byte[]>();
        var ownedDisposables = new List<IDisposable>();
        SftpClient? client = null;
        try
        {
            var authentication = await CreateAuthenticationAsync(
                endpoint.Username,
                ownedBuffers,
                ownedDisposables,
                cancellationToken).ConfigureAwait(false);
            ownedDisposables.Add(authentication);
            var connection = SshNetConnectionInfoFactory.Create(
                endpoint,
                endpoint.Username,
                authentication,
                networkConnector);
            connection.Timeout = TimeSpan.FromSeconds(15);
            connection.RetryAttempts = 1;
            client = new SftpClient(connection)
            {
                KeepAliveInterval = options.Connection.KeepAlive.Enabled
                    ? options.Connection.KeepAlive.Interval
                    : Timeout.InfiniteTimeSpan,
                OperationTimeout = TimeSpan.FromSeconds(15),
            };

            RemoteFileSessionErrorCode? hostKeyFailure = null;
            client.HostKeyReceived += (_, eventArgs) =>
            {
                var candidate = new SshHostKeyCandidate(
                    eventArgs.HostKeyName,
                    Convert.ToBase64String(eventArgs.HostKey));
                var decision = SftpHostKeyPolicyEvaluator.Evaluate(
                    options.Connection,
                    knownHosts,
                    candidate);
                hostKeyFailure = decision.Failure;
                eventArgs.CanTrust = decision.Trusted;
            };

            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (hostKeyFailure is { } failure)
            {
                throw failure switch
                {
                    RemoteFileSessionErrorCode.HostKeyChanged => new RemoteFileSessionException(
                        failure,
                        "The SFTP server host key changed. Review the SFTP provider before reconnecting.",
                        innerException: exception),
                    RemoteFileSessionErrorCode.HostKeyStoreInvalid => new RemoteFileSessionException(
                        failure,
                        "The trusted SSH host-key store is unavailable or malformed.",
                        innerException: exception),
                    _ => new RemoteFileSessionException(
                        failure,
                        "The SFTP server host key is not trusted. Review the SFTP provider before reconnecting.",
                        innerException: exception),
                };
            }

            return new SshNetSftpSession(client, ownedBuffers, ownedDisposables);
        }
        catch (Exception exception)
            when (exception is not RemoteFileSessionException
                && exception is not OperationCanceledException)
        {
            TryDispose(client);
            DisposeAuthentication(ownedBuffers, ownedDisposables);
            throw SshNetSftpSession.MapException(exception);
        }
        catch
        {
            TryDispose(client);
            DisposeAuthentication(ownedBuffers, ownedDisposables);
            throw;
        }
    }

    private async ValueTask<AuthenticationMethod> CreateAuthenticationAsync(
        string username,
        List<byte[]> ownedBuffers,
        List<IDisposable> ownedDisposables,
        CancellationToken cancellationToken)
    {
        switch (options.Connection.Authentication)
        {
            case ConnectionAuthentication.None:
            case ConnectionAuthentication.SshAgent:
                {
                    var identities = await _systemAuthentication
                        .GetIdentitiesAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (identities.Length == 0)
                    {
                        throw new RemoteFileSessionException(
                            RemoteFileSessionErrorCode.AuthenticationFailed,
                            "No SSH identity is available to the shared connection transport.");
                    }

                    return new PrivateKeyAuthenticationMethod(username, identities);
                }
            case ConnectionAuthentication.Password password:
                {
                    var bytes = await ResolveSecretAsync(
                        password.PasswordSecret,
                        cancellationToken).ConfigureAwait(false);
                    ownedBuffers.Add(bytes);
                    return new PasswordAuthenticationMethod(username, bytes);
                }
            case ConnectionAuthentication.PrivateKey privateKey:
                {
                    var keyBytes = await ResolveSecretAsync(
                        privateKey.PrivateKeySecret,
                        cancellationToken).ConfigureAwait(false);
                    ownedBuffers.Add(keyBytes);
                    string? passphrase = null;
                    if (privateKey.PassphraseSecret is { } passphraseReference)
                    {
                        var passphraseBytes = await ResolveSecretAsync(
                            passphraseReference,
                            cancellationToken).ConfigureAwait(false);
                        ownedBuffers.Add(passphraseBytes);
                        passphrase = Encoding.UTF8.GetString(passphraseBytes);
                    }

                    var keyStream = new MemoryStream(keyBytes, writable: false);
                    ownedDisposables.Add(keyStream);
                    var keyFile = passphrase is null
                        ? new PrivateKeyFile(keyStream)
                        : new PrivateKeyFile(keyStream, passphrase);
                    ownedDisposables.Add(keyFile);
                    return new PrivateKeyAuthenticationMethod(username, keyFile);
                }
            default:
                throw new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.InvalidConfiguration,
                    "The SSH authentication mode is invalid.");
        }
    }

    private async ValueTask<byte[]> ResolveSecretAsync(
        SecretRef reference,
        CancellationToken cancellationToken)
    {
        var scope = new SecretScope(SecretScopeKind.Connection, options.Connection.Id.Value);
        var purpose = new SecretUsePurpose(
            SecretUseKind.ConnectionAuthentication,
            options.Connection.Id.Value);
        var result = await secretVault.ResolveAsync(
            new ResolveSecretRequest(reference, scope, purpose),
            cancellationToken).ConfigureAwait(false);
        if (result is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            throw failure.Error.Code is SecretVaultErrorCode.Cancelled
                or SecretVaultErrorCode.UserCancelled
                ? new OperationCanceledException(cancellationToken)
                : new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.AuthenticationFailed,
                    "The SFTP credential could not be resolved.");
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);
        return bytes;
    }

    private static void DisposeAuthentication(
        IEnumerable<byte[]> buffers,
        IEnumerable<IDisposable> disposables)
    {
        try
        {
            foreach (var disposable in disposables.Reverse())
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // Cleanup must continue so every owned credential can be released.
                }
            }
        }
        finally
        {
            foreach (var buffer in buffers)
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private static void TryDispose(SftpClient? client)
    {
        try
        {
            client?.Dispose();
        }
        catch
        {
            // Connection cleanup must not replace the classified connection failure.
        }
    }

    internal static FileEntryKind ClassifyEntryKind(
        bool isSymbolicLink,
        bool isDirectory,
        bool isRegularFile,
        bool canonicalPathChanged = false) =>
        isSymbolicLink || canonicalPathChanged
            ? FileEntryKind.Link
            : isDirectory
                ? FileEntryKind.Directory
                : isRegularFile
                    ? FileEntryKind.File
                    : FileEntryKind.Other;

    internal static bool CanonicalPathChanged(string requestedPath, string canonicalPath) =>
        !string.Equals(
            NormalizeRemotePath(requestedPath),
            NormalizeRemotePath(canonicalPath),
            StringComparison.Ordinal);

    private static string NormalizeRemotePath(string path) =>
        path.Length > 1 ? path.TrimEnd('/') : path;

    private sealed class SshNetSftpSession(
        SftpClient client,
        IReadOnlyList<byte[]> ownedBuffers,
        IReadOnlyList<IDisposable> ownedDisposables) : IRetainableRemoteFileSession
    {
        private const int MaximumMetadataScanEntries = 100_000;
        private readonly SftpMetadataCache _metadata = new(
            TimeProvider.System,
            TimeSpan.FromSeconds(10),
            maximumEntries: 4_096);
        private bool _healthy = true;
        private bool _disposed;

        public bool CanReuse => !_disposed && _healthy && client.IsConnected;

        public bool StatDetectsAnyLinkInPath => true;

        public async ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = new RemoteDirectorySnapshot();
                await foreach (var entry in client
                    .ListDirectoryAsync(path, cancellationToken)
                    .ConfigureAwait(false))
                {
                    snapshot.Add(
                        ToRemoteEntry(entry.Name, entry.Attributes),
                        cancellationToken);
                }

                var entries = snapshot.Complete(cancellationToken);
                _metadata.StoreDirectory(path, entries);
                return entries;
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapSessionException(exception);
            }
        }

        public async ValueTask<RemoteFileEntry?> StatAsync(
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                if (_metadata.TryGet(path, out var cached))
                {
                    return cached;
                }

                var entry = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
                var result = ToRemoteEntry(
                    RemoteName(path),
                    entry.Attributes,
                    CanonicalPathChanged(path, entry.FullName));
                _metadata.Store(path, result);
                return result;
            }
            catch (SftpPathNotFoundException)
            {
                return null;
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapSessionException(exception);
            }
        }

        public async ValueTask<Stream> OpenReadAsync(
            string path,
            long offset,
            CancellationToken cancellationToken)
        {
            try
            {
                var stream = await client
                    .OpenAsync(path, FileMode.Open, FileAccess.Read, cancellationToken)
                    .ConfigureAwait(false);
                stream.Position = offset;
                return new ExceptionMappingStream(stream, MapSessionException);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapSessionException(exception);
            }
        }

        public async ValueTask<Stream> OpenCreateNewAsync(
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                _metadata.Clear();
                var stream = await client
                    .OpenAsync(path, FileMode.CreateNew, FileAccess.Write, cancellationToken)
                    .ConfigureAwait(false);
                return new ExceptionMappingStream(stream, MapSessionException);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapSessionException(exception);
            }
        }

        public async ValueTask CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken)
        {
            await ExecuteAsync(() => client.CreateDirectoryAsync(path, cancellationToken))
                .ConfigureAwait(false);
            _metadata.Clear();
        }

        /// <summary>
        /// SFTP carries the mode in the same attribute block as the size and
        /// the timestamps, so reading it is a stat. Writing it is one message
        /// with no async form in the client, and it is one round trip.
        /// </summary>
        public async ValueTask<int?> GetPermissionsAsync(
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                var attributes = await client
                    .GetAttributesAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                return attributes is null ? null : PosixMode(attributes);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapSessionException(exception);
            }
        }

        public ValueTask SetPermissionsAsync(
            string path,
            int mode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                client.ChangePermissions(path, (short)(mode & 0x1FF));
                _metadata.Clear();
                return ValueTask.CompletedTask;
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapSessionException(exception);
            }
        }

        public async ValueTask RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            await ExecuteAsync(
                    () => client.RenameFileAsync(
                        sourcePath,
                        destinationPath,
                        cancellationToken))
                .ConfigureAwait(false);
            _metadata.Clear();
        }

        public async ValueTask DeleteFileAsync(
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                var entry = await FindExactEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new RemoteFileSessionException(
                        RemoteFileSessionErrorCode.NotFound,
                        "The SFTP path was not found.");
                if (entry.IsDirectory)
                {
                    throw new RemoteFileSessionException(
                        RemoteFileSessionErrorCode.IsDirectory,
                        "The SFTP path is a directory.");
                }

                await entry.DeleteAsync(cancellationToken).ConfigureAwait(false);
                _metadata.Clear();
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapSessionException(exception);
            }
        }

        public async ValueTask DeleteDirectoryAsync(
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                var entry = await FindExactEntryAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new RemoteFileSessionException(
                        RemoteFileSessionErrorCode.NotFound,
                        "The SFTP path was not found.");
                if (!entry.IsDirectory)
                {
                    throw new RemoteFileSessionException(
                        RemoteFileSessionErrorCode.NotDirectory,
                        "The SFTP path is not a directory.");
                }

                await entry.DeleteAsync(cancellationToken).ConfigureAwait(false);
                _metadata.Clear();
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapSessionException(exception);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _metadata.Clear();
            try
            {
                client.Dispose();
            }
            catch (Exception)
            {
                _healthy = false;
            }
            finally
            {
                DisposeAuthentication(ownedBuffers, ownedDisposables);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private RemoteFileSessionException MapSessionException(Exception exception)
        {
            var mapped = MapException(exception);
            if (mapped.Retryable)
            {
                _healthy = false;
                _metadata.Clear();
            }

            return mapped;
        }

        internal static RemoteFileSessionException MapException(Exception exception) => exception switch
        {
            SftpPathNotFoundException => Error(RemoteFileSessionErrorCode.NotFound, "The SFTP path was not found."),
            SftpPermissionDeniedException => Error(RemoteFileSessionErrorCode.AccessDenied, "The SFTP server denied the operation."),
            SshAuthenticationException => Error(RemoteFileSessionErrorCode.AuthenticationFailed, "SFTP authentication failed."),
            SshOperationTimeoutException => Error(RemoteFileSessionErrorCode.Transient, "The SFTP operation timed out.", true),
            SshConnectionException => Error(RemoteFileSessionErrorCode.Transient, "The SFTP connection failed.", true),
            SocketException => Error(RemoteFileSessionErrorCode.Transient, "The SFTP network connection failed.", true),
            IOException => Error(RemoteFileSessionErrorCode.Transient, "The SFTP stream failed.", true),
            NotSupportedException => Error(RemoteFileSessionErrorCode.Unsupported, "The SFTP server does not support the operation."),
            SftpException => Error(RemoteFileSessionErrorCode.IoFailure, "The SFTP server rejected the operation."),
            _ => Error(RemoteFileSessionErrorCode.IoFailure, "The SFTP adapter failed."),
        };

        private static RemoteFileEntry ToRemoteEntry(
            string name,
            Renci.SshNet.Sftp.SftpFileAttributes attributes,
            bool canonicalPathChanged = false)
        {
            var kind = ClassifyEntryKind(
                attributes.IsSymbolicLink,
                attributes.IsDirectory,
                attributes.IsRegularFile,
                canonicalPathChanged);
            var mode = PosixMode(attributes);
            var revision = FormattableString.Invariant(
                $"sftp:{(int)kind}:{attributes.Size}:{attributes.LastWriteTimeUtc.Ticks}:{attributes.UserId}:{attributes.GroupId}:{mode}");
            return new RemoteFileEntry(
                name,
                kind,
                kind == FileEntryKind.File ? attributes.Size : null,
                new DateTimeOffset(attributes.LastWriteTimeUtc, TimeSpan.Zero),
                revision,
                new RemotePosixMetadata(attributes.UserId, attributes.GroupId, mode));
        }

        private static string RemoteName(string path)
        {
            var normalized = NormalizeRemotePath(path);
            if (string.Equals(normalized, "/", StringComparison.Ordinal))
            {
                return "/";
            }

            var separator = normalized.LastIndexOf('/');
            return separator < 0 ? normalized : normalized[(separator + 1)..];
        }

        private async ValueTask<Renci.SshNet.Sftp.ISftpFile?> FindExactEntryAsync(
            string path,
            CancellationToken cancellationToken)
        {
            if (string.Equals(path, "/", StringComparison.Ordinal))
            {
                return await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
            }

            var separator = path.LastIndexOf('/');
            var parent = separator <= 0 ? "/" : path[..separator];
            var name = path[(separator + 1)..];
            var inspected = 0;
            await foreach (var entry in client
                .ListDirectoryAsync(parent, cancellationToken)
                .ConfigureAwait(false))
            {
                if (++inspected > MaximumMetadataScanEntries)
                {
                    throw new RemoteFileSessionException(
                        RemoteFileSessionErrorCode.LimitExceeded,
                        "The SFTP directory exceeds the bounded metadata-scan limit.");
                }

                if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        private static int PosixMode(Renci.SshNet.Sftp.SftpFileAttributes attributes)
        {
            var mode = 0;
            if (attributes.OwnerCanRead)
            {
                mode |= 0x100;
            }

            if (attributes.OwnerCanWrite)
            {
                mode |= 0x80;
            }

            if (attributes.OwnerCanExecute)
            {
                mode |= 0x40;
            }

            if (attributes.GroupCanRead)
            {
                mode |= 0x20;
            }

            if (attributes.GroupCanWrite)
            {
                mode |= 0x10;
            }

            if (attributes.GroupCanExecute)
            {
                mode |= 0x8;
            }

            if (attributes.OthersCanRead)
            {
                mode |= 0x4;
            }

            if (attributes.OthersCanWrite)
            {
                mode |= 0x2;
            }

            if (attributes.OthersCanExecute)
            {
                mode |= 0x1;
            }

            return mode;
        }

        private async ValueTask ExecuteAsync(Func<Task> operation)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapSessionException(exception);
            }
        }

        private static bool ShouldMap(Exception exception) =>
            exception is not OperationCanceledException
            && exception is not RemoteFileSessionException;

        private static RemoteFileSessionException Error(
            RemoteFileSessionErrorCode code,
            string message,
            bool retryable = false) =>
            new(code, message, retryable);
    }
}
