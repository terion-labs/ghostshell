using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using GhostShell.Application;
using GhostShell.Core;
using RuntimeProfileId = GhostShell.Core.FileProviderProfileId;

namespace GhostShell.Files;

/// <summary>
/// Converts durable provider definitions into owned SDK adapters. Secret values are resolved and
/// consumed only in this boundary; registrations, diagnostics, and file locations remain opaque.
/// </summary>
internal sealed class FileProviderAdapterFactory(
    ISecretVault secretVault,
    ISshHostKeyTrustStore knownHosts,
    IConnectionRuntime? connectionRuntime = null,
    IWorkspaceNetworkConnector? networkConnector = null)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ValueTask<OwnedFileProviderRegistration> CreateAsync(
        FileProviderProfile profile,
        IReadOnlyDictionary<ConnectionId, ConnectionProfile> connections,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(connections);
        cancellationToken.ThrowIfCancellationRequested();
        var providerId = new FileProviderProfileId(profile.Id.Value);
        var registration = profile.Configuration switch
        {
            FileProviderConfiguration.Local local => CreateLocal(profile, providerId, local),
            FileProviderConfiguration.S3 s3 => CreateS3(profile, providerId, s3),
            FileProviderConfiguration.Sftp sftp => CreateSftp(
                profile,
                providerId,
                sftp,
                connections),
            FileProviderConfiguration.Ftp ftp => CreateFtp(profile, providerId, ftp),
            FileProviderConfiguration.Smb smb => CreateSmb(profile, providerId, smb),
            FileProviderConfiguration.WebDav webDav => CreateWebDav(profile, providerId, webDav),
            _ => throw InvalidConfiguration("The file-provider kind is not supported."),
        };
        return ValueTask.FromResult(registration);
    }

    private static OwnedFileProviderRegistration CreateLocal(
        FileProviderProfile profile,
        FileProviderProfileId providerId,
        FileProviderConfiguration.Local configuration)
    {
        if (!Path.IsPathFullyQualified(configuration.RootPath))
        {
            throw InvalidConfiguration("The local provider root must be an absolute path.");
        }

        var provider = LocalFileProvider.CreateForCurrentPlatform(new LocalFileProviderOptions(
            providerId,
            new FileAuthority("local"),
            configuration.RootPath));
        return Owned(
            profile,
            provider,
            HierarchicalRoot(provider),
            LocalFamily(),
            OperatingSystem.IsWindows()
                ? FilePanelCapability.None
                : FilePanelCapability.GovernedCreateDirectory
                    | FilePanelCapability.GovernedDelete
                    | FilePanelCapability.GovernedRename);
    }

    private OwnedFileProviderRegistration CreateS3(
        FileProviderProfile profile,
        FileProviderProfileId providerId,
        FileProviderConfiguration.S3 configuration)
    {
        var authority = new FileAuthority(configuration.BucketName);
        var deferred = new DeferredFileProvider(
            providerId,
            S3FileProvider.DefaultCapabilities,
            cancellationToken => MaterializeS3Async(
                profile.Id,
                providerId,
                authority,
                configuration,
                cancellationToken));
        return Owned(
            profile,
            deferred,
            CreateS3Root(providerId, authority, configuration.RootPrefix),
            FileProviderFamily.S3,
            FilePanelCapability.None,
            deferred);
    }

    private async ValueTask<MaterializedFileProvider> MaterializeS3Async(
        RuntimeProfileId durableProfileId,
        FileProviderProfileId providerId,
        FileAuthority authority,
        FileProviderConfiguration.S3 configuration,
        CancellationToken cancellationToken)
    {
        var clientConfiguration = CreateS3ClientConfiguration(configuration);
        if (networkConnector is not null)
        {
            clientConfiguration.HttpClientFactory =
                new WorkspaceConnectorHttpClientFactory(networkConnector);
        }

        AmazonS3Client client;
        if (configuration.CredentialsSecret is { } secretReference)
        {
            var credential = await ResolveS3CredentialAsync(
                durableProfileId,
                secretReference,
                cancellationToken).ConfigureAwait(false);
            client = new AmazonS3Client(credential, clientConfiguration);
        }
        else
        {
            // Profiles without an explicit SecretRef are intentionally anonymous; the desktop
            // never falls through to ambient process/environment credentials.
            client = new AmazonS3Client(new AnonymousAWSCredentials(), clientConfiguration);
        }

        try
        {
            var provider = new S3FileProvider(
                client,
                new S3FileProviderOptions(providerId, authority, configuration.BucketName));
            return new MaterializedFileProvider(provider, [client]);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    internal static AmazonS3Config CreateS3ClientConfiguration(
        FileProviderConfiguration.S3 configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var clientConfiguration = new AmazonS3Config
        {
            ForcePathStyle = configuration.ForcePathStyle,
            // A governed mutation has its own outcome-unknown state machine.
            // Hidden SDK replay would bypass its exactly-once dispatch boundary.
            MaxErrorRetry = 0,
            MaxStaleConnectionRetries = 0,
        };
        if (configuration.ServiceUri is { } serviceUri)
        {
            clientConfiguration.ServiceURL = serviceUri.AbsoluteUri;
            clientConfiguration.UseHttp = string.Equals(serviceUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);
            clientConfiguration.AuthenticationRegion = configuration.Region ?? "us-east-1";
        }
        else
        {
            clientConfiguration.RegionEndpoint = RegionEndpoint.GetBySystemName(
                configuration.Region ?? "us-east-1");
        }

        return clientConfiguration;
    }

    private OwnedFileProviderRegistration CreateSftp(
        FileProviderProfile profile,
        FileProviderProfileId providerId,
        FileProviderConfiguration.Sftp configuration,
        IReadOnlyDictionary<ConnectionId, ConnectionProfile> connections)
    {
        if (!connections.TryGetValue(configuration.ConnectionId, out var connection)
            || connection.Endpoint is not ConnectionEndpoint.Ssh)
        {
            throw InvalidConfiguration(
                "The SFTP provider references an unavailable or non-SSH connection profile.");
        }

        var options = new SftpFileProviderOptions(
            providerId,
            connection,
            configuration.RemoteRoot);
        var provider = new SftpFileProvider(
            secretVault,
            knownHosts,
            options,
            connectionRuntime,
            networkConnector);
        return Owned(
            profile,
            provider,
            HierarchicalRoot(provider),
            FileProviderFamily.Sftp,
            FilePanelCapability.None,
            provider);
    }

    private OwnedFileProviderRegistration CreateFtp(
        FileProviderProfile profile,
        FileProviderProfileId providerId,
        FileProviderConfiguration.Ftp configuration)
    {
        var security = configuration.Security switch
        {
            FtpSecurityMode.Plaintext => FtpTransportSecurity.Plaintext,
            FtpSecurityMode.ExplicitTls => FtpTransportSecurity.ExplicitTls,
            FtpSecurityMode.ImplicitTls => FtpTransportSecurity.ImplicitTls,
            _ => throw InvalidConfiguration("The FTP transport-security mode is invalid."),
        };
        var connectionMode = configuration.ConnectionMode switch
        {
            FtpConnectionMode.AutoPassive or FtpConnectionMode.Passive =>
                FtpDataConnectionMode.Passive,
            FtpConnectionMode.Active => FtpDataConnectionMode.Active,
            _ => throw InvalidConfiguration("The FTP data-connection mode is invalid."),
        };
        var options = new FtpFileProviderOptions(
            providerId,
            new FileAuthority(configuration.Host),
            configuration.Host,
            configuration.Username ?? "anonymous",
            configuration.PasswordSecret,
            security,
            connectionMode,
            configuration.Port,
            configuration.RemoteRoot);
        var provider = new FtpFileProvider(secretVault, options, networkConnector);
        return Owned(
            profile,
            provider,
            HierarchicalRoot(provider),
            FileProviderFamily.Ftp,
            FilePanelCapability.None);
    }

    private OwnedFileProviderRegistration CreateSmb(
        FileProviderProfile profile,
        FileProviderProfileId providerId,
        FileProviderConfiguration.Smb configuration)
    {
        SmbAuthentication authentication = configuration.CredentialMode switch
        {
            SmbCredentialMode.Guest => new SmbAuthentication.Guest(),
            SmbCredentialMode.UsernamePassword => new SmbAuthentication.Password(
                configuration.Domain ?? string.Empty,
                configuration.Username!,
                configuration.PasswordSecret!.Value),
            _ => throw InvalidConfiguration("The SMB authentication mode is invalid."),
        };
        var options = new SmbFileProviderOptions(
            providerId,
            new FileAuthority(configuration.Server),
            configuration.Server,
            configuration.Share,
            authentication,
            configuration.RemoteRoot);
        var provider = new SmbFileProvider(
            secretVault,
            options,
            networkConnector);
        return Owned(
            profile,
            provider,
            HierarchicalRoot(provider),
            FileProviderFamily.Smb,
            FilePanelCapability.None);
    }

    private OwnedFileProviderRegistration CreateWebDav(
        FileProviderProfile profile,
        FileProviderProfileId providerId,
        FileProviderConfiguration.WebDav configuration)
    {
        var authority = new FileAuthority(configuration.BaseUri.Host);
        var deferred = new DeferredFileProvider(
            providerId,
            WebDavFileProvider.DefaultCapabilities,
            cancellationToken => MaterializeWebDavAsync(
                profile.Id,
                providerId,
                authority,
                configuration,
                cancellationToken));
        return Owned(
            profile,
            deferred,
            new FileLocation(providerId, authority, FilePath.Root),
            FileProviderFamily.WebDav,
            FilePanelCapability.GovernedCreateDirectory
                | FilePanelCapability.GovernedCreateFile
                | FilePanelCapability.GovernedReplaceFile
                | FilePanelCapability.GovernedCopySource
                | FilePanelCapability.GovernedCopy,
            deferred);
    }

    private async ValueTask<MaterializedFileProvider> MaterializeWebDavAsync(
        RuntimeProfileId durableProfileId,
        FileProviderProfileId providerId,
        FileAuthority authority,
        FileProviderConfiguration.WebDav configuration,
        CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode =
                    System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
            },
            PreAuthenticate = true,
        };
        if (networkConnector is not null)
        {
            handler.ConnectCallback = async (context, token) =>
                await networkConnector.ConnectTcpAsync(
                        context.DnsEndPoint.Host,
                        context.DnsEndPoint.Port,
                        token)
                    .ConfigureAwait(false);
        }
        if (configuration.PasswordSecret is { } secretReference)
        {
            var password = await ResolveTextSecretAsync(
                durableProfileId,
                secretReference,
                cancellationToken).ConfigureAwait(false);
            handler.Credentials = new NetworkCredential(configuration.Username, password);
        }

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        try
        {
            var provider = new WebDavFileProvider(
                client,
                new WebDavFileProviderOptions(providerId, authority, configuration.BaseUri));
            return new MaterializedFileProvider(provider, [client]);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async ValueTask<AWSCredentials> ResolveS3CredentialAsync(
        RuntimeProfileId profileId,
        SecretRef reference,
        CancellationToken cancellationToken)
    {
        var bytes = await ResolveSecretBytesAsync(profileId, reference, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var accessKeyId = RequiredJsonString(root, "accessKeyId");
            var secretAccessKey = RequiredJsonString(root, "secretAccessKey");
            var sessionToken = OptionalJsonString(root, "sessionToken");
            return sessionToken is null
                ? new BasicAWSCredentials(accessKeyId, secretAccessKey)
                : new SessionAWSCredentials(accessKeyId, secretAccessKey, sessionToken);
        }
        catch (JsonException)
        {
            throw InvalidConfiguration(
                "The S3 credential must be a JSON object containing accessKeyId and secretAccessKey.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async ValueTask<string> ResolveTextSecretAsync(
        RuntimeProfileId profileId,
        SecretRef reference,
        CancellationToken cancellationToken)
    {
        var bytes = await ResolveSecretBytesAsync(profileId, reference, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw InvalidConfiguration("The provider credential must contain valid UTF-8 text.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async ValueTask<byte[]> ResolveSecretBytesAsync(
        RuntimeProfileId profileId,
        SecretRef reference,
        CancellationToken cancellationToken)
    {
        var targetId = profileId.Value;
        var result = await secretVault.ResolveAsync(
            new ResolveSecretRequest(
                reference,
                new SecretScope(SecretScopeKind.FileProvider, targetId),
                new SecretUsePurpose(SecretUseKind.FileProviderAuthentication, targetId)),
            cancellationToken).ConfigureAwait(false);
        if (result is SecretVaultResult<SecretMaterial>.Failure failure)
        {
            if (failure.Error.Code is SecretVaultErrorCode.Cancelled
                or SecretVaultErrorCode.UserCancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw InvalidConfiguration("The provider credential could not be resolved from the OS vault.");
        }

        using var material = ((SecretVaultResult<SecretMaterial>.Success)result).Value;
        var bytes = new byte[material.Length];
        material.CopyTo(bytes);
        return bytes;
    }

    private static string RequiredJsonString(JsonElement root, string propertyName) =>
        OptionalJsonString(root, propertyName)
        ?? throw InvalidConfiguration($"The S3 credential is missing '{propertyName}'.");

    private static string? OptionalJsonString(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static FileLocation CreateS3Root(
        FileProviderProfileId providerId,
        FileAuthority authority,
        string? configuredPrefix)
    {
        if (string.IsNullOrEmpty(configuredPrefix))
        {
            return FileLocation.ForContainerRoot(providerId, authority);
        }

        if (configuredPrefix.StartsWith('/'))
        {
            throw InvalidConfiguration(
                "An S3 initial prefix cannot start with '/'; exact leading-slash keys are not hierarchical browser roots.");
        }

        var normalized = configuredPrefix.TrimEnd('/');
        if (normalized.Contains("//", StringComparison.Ordinal))
        {
            throw InvalidConfiguration(
                "An S3 initial prefix cannot contain empty hierarchical segments.");
        }

        var segments = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => new FilePathSegment(value));
        return new FileLocation(
            providerId,
            authority,
            FilePath.FromSegments(segments));
    }

    private static FileLocation HierarchicalRoot(IFileProvider provider) =>
        new(provider.ProfileId, ProviderAuthority(provider), FilePath.Root);

    private static FileAuthority? ProviderAuthority(IFileProvider provider) => provider switch
    {
        LocalFileProvider local => local.Authority,
        RemoteHierarchicalFileProvider remote => remote.Authority,
        WebDavFileProvider webDav => webDav.Authority,
        S3FileProvider s3 => s3.Authority,
        _ => null,
    };

    private static OwnedFileProviderRegistration Owned(
        FileProviderProfile profile,
        IFileProvider provider,
        FileLocation root,
        FileProviderFamily family,
        FilePanelCapability governedMutationCapabilities,
        params IDisposable[] owners) =>
        new(
            profile.Id,
            new FileProviderRegistration(
                profile.Name,
                family,
                provider,
                root,
                governedMutationCapabilities),
            owners);

    private static FileProviderFamily LocalFamily() => OperatingSystem.IsWindows()
        ? FileProviderFamily.Windows
        : FileProviderFamily.Posix;

    private static FileProviderAdapterConfigurationException InvalidConfiguration(string message) =>
        new(message);

    private sealed class WorkspaceConnectorHttpClientFactory(
        IWorkspaceNetworkConnector networkConnector) : Amazon.Runtime.HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            ArgumentNullException.ThrowIfNull(clientConfig);
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = clientConfig.AllowAutoRedirect,
                ConnectCallback = async (context, cancellationToken) =>
                    await networkConnector.ConnectTcpAsync(
                            context.DnsEndPoint.Host,
                            context.DnsEndPoint.Port,
                            cancellationToken)
                        .ConfigureAwait(false),
            };
            return new HttpClient(handler, disposeHandler: true);
        }

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;
    }
}

internal sealed record OwnedFileProviderRegistration(
    RuntimeProfileId DurableProfileId,
    FileProviderRegistration Registration,
    IReadOnlyList<IDisposable> Owners) : IDisposable
{
    public void Dispose()
    {
        foreach (var owner in Owners.Reverse())
        {
            owner.Dispose();
        }
    }
}

internal sealed class FileProviderAdapterConfigurationException(string message) : Exception(message)
{
}
