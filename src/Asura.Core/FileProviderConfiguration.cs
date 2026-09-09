using System.Text.Json.Serialization;

namespace Asura.Core;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(FileProviderConfiguration.Local), "local")]
[JsonDerivedType(typeof(FileProviderConfiguration.S3), "s3")]
[JsonDerivedType(typeof(FileProviderConfiguration.Sftp), "sftp")]
[JsonDerivedType(typeof(FileProviderConfiguration.Ftp), "ftp")]
[JsonDerivedType(typeof(FileProviderConfiguration.Smb), "smb")]
[JsonDerivedType(typeof(FileProviderConfiguration.WebDav), "webdav")]
public abstract record FileProviderConfiguration : IPanelLaunchCapabilitySource
{
    private FileProviderConfiguration()
    {
    }

    [JsonIgnore]
    public abstract FileProviderKind Kind { get; }

    [JsonIgnore]
    public PanelLaunchCapabilities PanelLaunchCapabilities =>
        BuiltInPanelLaunchCapabilities.FilesOnly;

    public sealed record Local : FileProviderConfiguration
    {
        [JsonConstructor]
        public Local(string rootPath)
        {
            RootPath = RequireBounded(rootPath, nameof(rootPath), 32_768);
        }

        public string RootPath { get; }

        public override FileProviderKind Kind => FileProviderKind.Local;
    }

    public sealed record S3 : FileProviderConfiguration
    {
        [JsonConstructor]
        public S3(
            string bucketName,
            string? region = null,
            Uri? serviceUri = null,
            bool forcePathStyle = false,
            bool allowInsecureTransport = false,
            SecretRef? credentialsSecret = null,
            string? rootPrefix = null)
        {
            BucketName = RequireBounded(bucketName, nameof(bucketName), 255);
            Region = NormalizeOptional(region, nameof(region), 255);
            ServiceUri = ValidateHttpUri(
                serviceUri,
                nameof(serviceUri),
                allowInsecureTransport,
                required: false);
            CredentialsSecret = ValidateSecret(credentialsSecret, nameof(credentialsSecret));
            RootPrefix = NormalizeOptional(rootPrefix, nameof(rootPrefix), 1_024, trim: false);
            if (RootPrefix?.Contains('\0', StringComparison.Ordinal) == true)
            {
                throw new ArgumentException("An S3 root prefix cannot contain a null character.", nameof(rootPrefix));
            }

            ForcePathStyle = forcePathStyle;
            AllowInsecureTransport = allowInsecureTransport;
        }

        public string BucketName { get; }

        public string? Region { get; }

        public Uri? ServiceUri { get; }

        public bool ForcePathStyle { get; }

        public bool AllowInsecureTransport { get; }

        public SecretRef? CredentialsSecret { get; }

        public string? RootPrefix { get; }

        public override FileProviderKind Kind => FileProviderKind.S3;
    }

    public sealed record Sftp : FileProviderConfiguration
    {
        [JsonConstructor]
        public Sftp(ConnectionId connectionId, string remoteRoot = "/")
        {
            RuntimeId.Require(connectionId.Value, nameof(connectionId));
            ConnectionId = connectionId;
            RemoteRoot = ValidateRemoteRoot(remoteRoot, nameof(remoteRoot));
        }

        public ConnectionId ConnectionId { get; }

        public string RemoteRoot { get; }

        public override FileProviderKind Kind => FileProviderKind.Sftp;
    }

    public sealed record Ftp : FileProviderConfiguration
    {
        [JsonConstructor]
        public Ftp(
            string host,
            int port,
            string? username,
            SecretRef? passwordSecret,
            FtpSecurityMode security,
            FtpConnectionMode connectionMode,
            string remoteRoot = "/",
            bool allowPlaintext = false)
        {
            Host = RequireHost(host, nameof(host));
            if (port is < 1 or > 65_535)
            {
                throw new ArgumentOutOfRangeException(nameof(port), port, null);
            }

            if (!Enum.IsDefined(security))
            {
                throw new ArgumentOutOfRangeException(nameof(security), security, null);
            }

            if (!Enum.IsDefined(connectionMode))
            {
                throw new ArgumentOutOfRangeException(nameof(connectionMode), connectionMode, null);
            }

            if (security == FtpSecurityMode.Plaintext && !allowPlaintext)
            {
                throw new ArgumentException(
                    "Plaintext FTP requires an explicit insecure-transport acknowledgement.",
                    nameof(allowPlaintext));
            }

            Username = NormalizeOptional(username, nameof(username), 255);
            PasswordSecret = ValidateSecret(passwordSecret, nameof(passwordSecret));
            if ((Username is null) != (PasswordSecret is null))
            {
                throw new ArgumentException(
                    "FTP username and password secret must either both be present or both be absent.",
                    nameof(username));
            }

            Port = port;
            Security = security;
            ConnectionMode = connectionMode;
            RemoteRoot = ValidateRemoteRoot(remoteRoot, nameof(remoteRoot));
            AllowPlaintext = allowPlaintext;
        }

        public string Host { get; }

        public int Port { get; }

        public string? Username { get; }

        public SecretRef? PasswordSecret { get; }

        public FtpSecurityMode Security { get; }

        public FtpConnectionMode ConnectionMode { get; }

        public string RemoteRoot { get; }

        public bool AllowPlaintext { get; }

        public override FileProviderKind Kind => FileProviderKind.Ftp;
    }

    public sealed record Smb : FileProviderConfiguration
    {
        [JsonConstructor]
        public Smb(
            string server,
            string share,
            SmbCredentialMode credentialMode,
            string? domain = null,
            string? username = null,
            SecretRef? passwordSecret = null,
            string remoteRoot = "/")
        {
            Server = RequireHost(server, nameof(server));
            Share = RequireOpaqueName(share, nameof(share));
            if (!Enum.IsDefined(credentialMode))
            {
                throw new ArgumentOutOfRangeException(nameof(credentialMode), credentialMode, null);
            }

            Domain = NormalizeOptional(domain, nameof(domain), 255);
            Username = NormalizeOptional(username, nameof(username), 255);
            PasswordSecret = ValidateSecret(passwordSecret, nameof(passwordSecret));
            if (credentialMode == SmbCredentialMode.Guest
                && (Domain is not null || Username is not null || PasswordSecret is not null))
            {
                throw new ArgumentException(
                    "Guest SMB authentication cannot include credentials.",
                    nameof(credentialMode));
            }

            if (credentialMode == SmbCredentialMode.UsernamePassword
                && (Username is null || PasswordSecret is null))
            {
                throw new ArgumentException(
                    "SMB username/password authentication requires a username and password secret.",
                    nameof(username));
            }

            CredentialMode = credentialMode;
            RemoteRoot = ValidateRemoteRoot(remoteRoot, nameof(remoteRoot));
        }

        public string Server { get; }

        public string Share { get; }

        public SmbCredentialMode CredentialMode { get; }

        public string? Domain { get; }

        public string? Username { get; }

        public SecretRef? PasswordSecret { get; }

        public string RemoteRoot { get; }

        public override FileProviderKind Kind => FileProviderKind.Smb;
    }

    public sealed record WebDav : FileProviderConfiguration
    {
        [JsonConstructor]
        public WebDav(
            Uri baseUri,
            string? username = null,
            SecretRef? passwordSecret = null,
            bool allowInsecureTransport = false)
        {
            BaseUri = ValidateHttpUri(
                baseUri,
                nameof(baseUri),
                allowInsecureTransport,
                required: true)!;
            Username = NormalizeOptional(username, nameof(username), 255);
            PasswordSecret = ValidateSecret(passwordSecret, nameof(passwordSecret));
            if ((Username is null) != (PasswordSecret is null))
            {
                throw new ArgumentException(
                    "WebDAV username and password secret must either both be present or both be absent.",
                    nameof(username));
            }

            AllowInsecureTransport = allowInsecureTransport;
        }

        public Uri BaseUri { get; }

        public string? Username { get; }

        public SecretRef? PasswordSecret { get; }

        public bool AllowInsecureTransport { get; }

        public override FileProviderKind Kind => FileProviderKind.WebDav;
    }

    private static string RequireBounded(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException("The value is not a bounded printable string.", parameterName);
        }

        return value;
    }

    private static string RequireHost(string value, string parameterName)
    {
        var host = RequireBounded(value, parameterName, 255).Trim();
        if (host.Any(character => character is '/' or '\\'))
        {
            throw new ArgumentException("A host cannot contain path separators.", parameterName);
        }

        return host;
    }

    private static string RequireOpaqueName(string value, string parameterName)
    {
        var name = RequireBounded(value, parameterName, 255).Trim();
        if (name.Any(character => character is '/' or '\\'))
        {
            throw new ArgumentException("The name cannot contain path separators.", parameterName);
        }

        return name;
    }

    private static string ValidateRemoteRoot(string value, string parameterName)
    {
        var root = RequireBounded(value, parameterName, 32_768);
        if (root[0] != '/')
        {
            throw new ArgumentException("A remote root must be absolute.", parameterName);
        }

        return root;
    }

    private static string? NormalizeOptional(
        string? value,
        string parameterName,
        int maximumLength,
        bool trim = true)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = trim ? value.Trim() : value;
        return RequireBounded(normalized, parameterName, maximumLength);
    }

    private static SecretRef? ValidateSecret(SecretRef? value, string parameterName)
    {
        if (value is { } reference)
        {
            RuntimeId.Require(reference.Value, parameterName);
        }

        return value;
    }

    private static Uri? ValidateHttpUri(
        Uri? value,
        string parameterName,
        bool allowInsecureTransport,
        bool required)
    {
        if (value is null)
        {
            return required
                ? throw new ArgumentNullException(parameterName)
                : null;
        }

        if (!value.IsAbsoluteUri || value.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The endpoint must be an absolute HTTP(S) URI.", parameterName);
        }

        if (!string.IsNullOrEmpty(value.UserInfo)
            || !string.IsNullOrEmpty(value.Query)
            || !string.IsNullOrEmpty(value.Fragment))
        {
            throw new ArgumentException(
                "The endpoint cannot contain credentials, a query, or a fragment.",
                parameterName);
        }

        if (string.Equals(value.Scheme, "http", StringComparison.Ordinal) && !allowInsecureTransport)
        {
            throw new ArgumentException(
                "HTTP transport requires an explicit insecure-transport acknowledgement.",
                parameterName);
        }

        return value;
    }
}
