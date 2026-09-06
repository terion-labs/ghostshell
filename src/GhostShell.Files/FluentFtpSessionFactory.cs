using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using FluentFTP;
using FluentFTP.Exceptions;
using FluentFTP.Proxy.AsyncProxy;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Files;

/// <summary>Maps the frozen provider seam to FluentFTP without leaking its SDK types.</summary>
internal sealed class FluentFtpSessionFactory(
    ISecretVault secretVault,
    FtpFileProviderOptions options,
    IWorkspaceNetworkConnector? networkConnector = null) :
    IRemoteHierarchicalFileSessionFactory,
    IFtpFeatureSource
{
    private FtpConnectionSnapshot? _lastConnection;

    public FtpConnectionSnapshot? LastConnection => Volatile.Read(ref _lastConnection);

    public async ValueTask<IRemoteHierarchicalFileSession> OpenAsync(
        CancellationToken cancellationToken)
    {
        var password = await ResolvePasswordAsync(cancellationToken).ConfigureAwait(false);
        if (!options.CanEncodeCredential(password))
        {
            throw new RemoteFileSessionException(
                RemoteFileSessionErrorCode.AuthenticationFailed,
                "The FTP credential is incompatible with the configured control-channel encoding.");
        }

        var config = CreateConfig(options);
        var credentials = new NetworkCredential(options.Username, password);
        AsyncFtpClient client = networkConnector is null
            ? new AsyncFtpClient(
                options.Host,
                credentials,
                options.Port,
                config,
                logger: null)
            : new AsyncFtpClientSocks5Proxy(new FtpProxyProfile
            {
                ProxyHost = networkConnector.LocalProxyEndpoint.Host,
                ProxyPort = networkConnector.LocalProxyEndpoint.Port,
                ProxyCredentials = networkConnector.LocalProxyCredentials is { } proxyCredentials
                    ? new NetworkCredential(
                        proxyCredentials.Username,
                        proxyCredentials.Password)
                    : null,
                FtpHost = options.Host,
                FtpPort = options.Port,
                FtpCredentials = credentials,
            })
            {
                Config = config,
            };
        client.Encoding = options.ControlEncoding;

        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            if (!client.IsAuthenticated)
            {
                throw new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.AuthenticationFailed,
                    "FTP authentication failed.");
            }

            var requiresTls = options.TransportSecurity != FtpTransportSecurity.Plaintext;
            if (requiresTls && !client.IsEncrypted)
            {
                throw new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.SecureTransportUnavailable,
                    "The FTP server did not establish the required TLS transport.");
            }

            var snapshot = new FtpConnectionSnapshot(
                options.TransportSecurity,
                client.IsEncrypted,
                MapFeatures(client.Capabilities),
                client.Encoding.WebName);
            Volatile.Write(ref _lastConnection, snapshot);
            return new FluentFtpSession(client, snapshot.ServerFeatures);
        }
        catch (Exception exception)
            when (exception is not RemoteFileSessionException
                && exception is not OperationCanceledException)
        {
            TryDispose(client);
            throw FluentFtpSession.MapException(exception);
        }
        catch
        {
            TryDispose(client);
            throw;
        }
    }

    internal static FtpConfig CreateConfig(FtpFileProviderOptions providerOptions) => new()
    {
        CheckCapabilities = true,
        ConnectTimeout = 15_000,
        ReadTimeout = 15_000,
        WriteTimeout = 15_000,
        DataConnectionConnectTimeout = 15_000,
        DataConnectionReadTimeout = 15_000,
        DataConnectionWriteTimeout = 15_000,
        DataConnectionType = providerOptions.DataConnectionMode == FtpDataConnectionMode.Passive
            ? FtpDataConnectionType.AutoPassive
            : FtpDataConnectionType.AutoActive,
        EncryptionMode = providerOptions.TransportSecurity switch
        {
            FtpTransportSecurity.Plaintext => FtpEncryptionMode.None,
            FtpTransportSecurity.ExplicitTls => FtpEncryptionMode.Explicit,
            FtpTransportSecurity.ImplicitTls => FtpEncryptionMode.Implicit,
            _ => throw new ArgumentOutOfRangeException(nameof(providerOptions.TransportSecurity)),
        },
        DataConnectionEncryption = providerOptions.TransportSecurity != FtpTransportSecurity.Plaintext,
        SslProtocols = SslProtocols.None,
        ValidateAnyCertificate = false,
        // FluentFTP requires at least one verification attempt. GhostSHELL uses streaming
        // OpenRead/OpenWrite rather than the SDK's verifying upload/download helpers, so this
        // setting cannot replay a provider mutation.
        RetryAttempts = 1,
        UploadDataType = FtpDataType.Binary,
        DownloadDataType = FtpDataType.Binary,
        TimeConversion = FtpDate.ServerTime,
        BulkListing = false,

        // GhostSHELL validates hierarchical segments before the vendor boundary. FluentFTP's
        // mutating path sanitizer would decode literal "%2e%2e" names or truncate names and
        // could thereby redirect an operation outside the configured remote root.
        SanitizeUrlEncoding = false,
        SanitizeTraversal = false,
        SanitizeControlChars = false,
        SanitizeMultiline = false,
        SanitizeUnicodeSpoofing = false,
    };

    private static void TryDispose(AsyncFtpClient client)
    {
        try
        {
            client.Dispose();
        }
        catch
        {
            // Connection cleanup must not replace the classified connection failure.
        }
    }

    private async ValueTask<string> ResolvePasswordAsync(CancellationToken cancellationToken)
    {
        if (options.PasswordSecret is not { } reference)
        {
            return string.Empty;
        }

        var targetId = options.ProfileId.Value;
        var result = await secretVault.ResolveAsync(
            new ResolveSecretRequest(
                reference,
                new SecretScope(SecretScopeKind.FileProvider, targetId),
                new SecretUsePurpose(SecretUseKind.FileProviderAuthentication, targetId)),
            cancellationToken).ConfigureAwait(false);
        if (result is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            throw failure.Error.Code is SecretVaultErrorCode.Cancelled
                or SecretVaultErrorCode.UserCancelled
                ? new OperationCanceledException(cancellationToken)
                : new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.AuthenticationFailed,
                    "The FTP credential could not be resolved.");
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        var bytes = new byte[material.Length];
        try
        {
            material.CopyTo(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static RemoteFileEntry MapListedEntry(FtpListItem item) =>
        MapEntry(item, item.Type == FtpObjectType.File && item.Size > 0 ? item.Size : null);

    internal static RemoteFileEntry MapStatEntry(FtpListItem item, long? knownSize) =>
        MapEntry(item, knownSize);

    private static RemoteFileEntry MapEntry(FtpListItem item, long? knownSize)
    {
        var kind = item.Type switch
        {
            FtpObjectType.Directory => FileEntryKind.Directory,
            FtpObjectType.Link => FileEntryKind.Link,
            FtpObjectType.File => FileEntryKind.File,
            _ => FileEntryKind.Other,
        };
        var modified = item.Modified.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(item.Modified)
            : (DateTimeOffset?)null;
        var size = kind == FileEntryKind.File ? knownSize : null;
        var rawListingSize = kind == FileEntryKind.File && item.Size >= 0 ? item.Size : -1;
        var revision = FormattableString.Invariant(
            $"ftp:{(int)kind}:{rawListingSize}:{modified?.UtcTicks ?? 0}");
        return new RemoteFileEntry(item.Name, kind, size, modified, revision);
    }

    private static FtpServerFeature MapFeatures(IReadOnlyCollection<FtpCapability> capabilities)
    {
        var features = FtpServerFeature.None;
        if (capabilities.Contains(FtpCapability.MLST))
        {
            features |= FtpServerFeature.MachineListing;
        }

        if (capabilities.Contains(FtpCapability.SIZE))
        {
            features |= FtpServerFeature.Size;
        }

        if (capabilities.Contains(FtpCapability.MDTM))
        {
            features |= FtpServerFeature.ModifiedTime;
        }

        if (capabilities.Contains(FtpCapability.REST))
        {
            features |= FtpServerFeature.RestartDownload;
        }

        if (capabilities.Contains(FtpCapability.UTF8))
        {
            features |= FtpServerFeature.Utf8;
        }

        if (capabilities.Any(capability => capability is FtpCapability.HASH
            or FtpCapability.MD5
            or FtpCapability.XMD5
            or FtpCapability.MMD5
            or FtpCapability.XSHA1
            or FtpCapability.XSHA256
            or FtpCapability.XSHA512
            or FtpCapability.XCRC))
        {
            features |= FtpServerFeature.Checksum;
        }

        return features;
    }

    private sealed class FluentFtpSession(
        AsyncFtpClient client,
        FtpServerFeature features) : IRemoteHierarchicalFileSession
    {
        private const int MaximumMetadataScanEntries = 100_000;
        private const long MaximumFallbackSkipBytes = 64L * 1024 * 1024;

        public async ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                var snapshot = new RemoteDirectorySnapshot();
                await foreach (var entry in client
                    .GetListingEnumerable(path, cancellationToken, cancellationToken)
                    .ConfigureAwait(false))
                {
                    snapshot.Add(MapListedEntry(entry), cancellationToken);
                }

                return snapshot.Complete(cancellationToken);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapException(exception);
            }
        }

        public async ValueTask<RemoteFileEntry?> StatAsync(
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                var entry = await FindEntryByListingAsync(path, cancellationToken).ConfigureAwait(false);
                if (entry is not null)
                {
                    long? knownSize = null;
                    if (entry.Type == FtpObjectType.File)
                    {
                        if (features.HasFlag(FtpServerFeature.Size))
                        {
                            var reportedSize = await client
                                .GetFileSize(path, defaultValue: -1, cancellationToken)
                                .ConfigureAwait(false);
                            if (reportedSize >= 0)
                            {
                                knownSize = reportedSize;
                            }
                        }

                        if (knownSize is null && entry.Size > 0)
                        {
                            knownSize = entry.Size;
                        }
                    }

                    return MapStatEntry(entry, knownSize);
                }

                if (string.Equals(path, "/"
, StringComparison.Ordinal) && await client.DirectoryExists(path, cancellationToken).ConfigureAwait(false))
                {
                    return SyntheticDirectory(path);
                }

                return null;
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapException(exception);
            }
        }

        public async ValueTask<Stream> OpenReadAsync(
            string path,
            long offset,
            CancellationToken cancellationToken)
        {
            try
            {
                var supportsRestart = features.HasFlag(FtpServerFeature.RestartDownload);
                if (!supportsRestart && offset > MaximumFallbackSkipBytes)
                {
                    throw new RemoteFileSessionException(
                        RemoteFileSessionErrorCode.LimitExceeded,
                        "The FTP server cannot efficiently satisfy the requested range offset.");
                }

                var remoteOffset = supportsRestart ? offset : 0;
                var stream = await client
                    .OpenRead(path, FtpDataType.Binary, remoteOffset, checkIfFileExists: false, cancellationToken)
                    .ConfigureAwait(false);
                if (!supportsRestart && offset > 0)
                {
                    await RemoteFileProviderUtilities.SkipAsync(
                        stream,
                        offset,
                        bufferSize: 64 * 1024,
                        cancellationToken).ConfigureAwait(false);
                }

                return new ExceptionMappingStream(stream, MapException);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapException(exception);
            }
        }

        public async ValueTask<Stream> OpenCreateNewAsync(
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                var stream = await client
                    .OpenWrite(path, FtpDataType.Binary, checkIfFileExists: false, cancellationToken)
                    .ConfigureAwait(false);
                return new ExceptionMappingStream(stream, MapException);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapException(exception);
            }
        }

        public async ValueTask CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                var created = await client.CreateDirectory(path, cancellationToken).ConfigureAwait(false);
                if (!created)
                {
                    throw new RemoteFileSessionException(
                        RemoteFileSessionErrorCode.AlreadyExists,
                        "The FTP directory could not be created.");
                }
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapException(exception);
            }
        }

        public async ValueTask RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            try
            {
                await client.Rename(sourcePath, destinationPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapException(exception);
            }
        }

        public ValueTask DeleteFileAsync(string path, CancellationToken cancellationToken) =>
            ExecuteAsync(() => client.DeleteFile(path, cancellationToken));

        public async ValueTask DeleteDirectoryAsync(
            string path,
            CancellationToken cancellationToken)
        {
            try
            {
                var reply = await client
                    .Execute($"RMD {path}", cancellationToken)
                    .ConfigureAwait(false);
                if (!reply.Success)
                {
                    throw new FtpCommandException(reply);
                }
            }
            catch (FtpCommandException exception) when (exception.CompletionCode.StartsWith("550", StringComparison.Ordinal))
            {
                throw new RemoteFileSessionException(
                    RemoteFileSessionErrorCode.IoFailure,
                    "The FTP server refused to remove the empty directory.");
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapException(exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (client.IsConnected)
                {
                    await client.Disconnect(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapException(exception);
            }
            finally
            {
                TryDispose(client);
            }
        }

        internal static RemoteFileSessionException MapException(Exception exception) => exception switch
        {
            FtpAuthenticationException => Error(RemoteFileSessionErrorCode.AuthenticationFailed, "FTP authentication failed."),
            FtpInvalidCertificateException => Error(RemoteFileSessionErrorCode.CertificateRejected, "The FTPS certificate was rejected."),
            AuthenticationException => Error(RemoteFileSessionErrorCode.CertificateRejected, "The FTPS handshake failed."),
            FtpSecurityNotAvailableException => Error(RemoteFileSessionErrorCode.SecureTransportUnavailable, "The FTP server does not support the required TLS mode."),
            FtpMissingObjectException => Error(RemoteFileSessionErrorCode.NotFound, "The FTP path was not found."),
            FtpCommandException command when command.CompletionCode.StartsWith("530", StringComparison.Ordinal) =>
                Error(RemoteFileSessionErrorCode.AuthenticationFailed, "The FTP server rejected authentication."),
            FtpCommandException command when command.CompletionCode.StartsWith("550", StringComparison.Ordinal) =>
                Error(RemoteFileSessionErrorCode.IoFailure, "The FTP server ambiguously rejected the file operation."),
            FtpCommandException command when command.CompletionCode.StartsWith('4') =>
                Error(RemoteFileSessionErrorCode.Transient, "The FTP server temporarily rejected the operation.", true),
            TimeoutException => Error(RemoteFileSessionErrorCode.Transient, "The FTP operation timed out.", true),
            SocketException => Error(RemoteFileSessionErrorCode.Transient, "The FTP network connection failed.", true),
            IOException => Error(RemoteFileSessionErrorCode.Transient, "The FTP stream failed.", true),
            NotSupportedException => Error(RemoteFileSessionErrorCode.Unsupported, "The FTP server does not support the operation."),
            FtpException => Error(RemoteFileSessionErrorCode.IoFailure, "The FTP server rejected the operation."),
            _ => Error(RemoteFileSessionErrorCode.IoFailure, "The FTP adapter failed."),
        };

        private static RemoteFileEntry SyntheticDirectory(string path)
        {
            var name = string.Equals(path, "/"
, StringComparison.Ordinal) ? string.Empty
                : path.TrimEnd('/').Split('/')[^1];
            return new RemoteFileEntry(name, FileEntryKind.Directory, null, null, $"ftp-directory:{path}");
        }

        private async ValueTask<FtpListItem?> FindEntryByListingAsync(
            string path,
            CancellationToken cancellationToken)
        {
            if (string.Equals(path, "/", StringComparison.Ordinal))
            {
                return null;
            }

            var separator = path.LastIndexOf('/');
            var parent = separator <= 0 ? "/" : path[..separator];
            var name = path[(separator + 1)..];
            var inspected = 0;
            await foreach (var entry in client
                .GetListingEnumerable(parent, cancellationToken, cancellationToken)
                .ConfigureAwait(false))
            {
                if (++inspected > MaximumMetadataScanEntries)
                {
                    throw new RemoteFileSessionException(
                        RemoteFileSessionErrorCode.LimitExceeded,
                        "The FTP directory exceeds the bounded metadata-scan limit.");
                }

                if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        private static async ValueTask ExecuteAsync(Func<Task> operation)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception exception) when (ShouldMap(exception))
            {
                throw MapException(exception);
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
