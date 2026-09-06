using GhostShell.Application;

namespace GhostShell.Files;

/// <summary>FTP, explicit FTPS, or implicit FTPS provider backed by FluentFTP.</summary>
public sealed class FtpFileProvider : RemoteHierarchicalFileProvider
{
    private static readonly FileProviderLimits ProviderLimits = new(
        maximumListPageSize: 1_000,
        maximumReadBytes: 64L * 1024 * 1024,
        maximumBufferSize: 1024 * 1024);
    private readonly IFtpFeatureSource? _featureSource;

    public FtpFileProvider(ISecretVault secretVault, FtpFileProviderOptions options)
        : this(secretVault, options, networkConnector: null)
    {
    }

    internal FtpFileProvider(
        ISecretVault secretVault,
        FtpFileProviderOptions options,
        IWorkspaceNetworkConnector? networkConnector)
        : this(
            new FluentFtpSessionFactory(
                secretVault ?? throw new ArgumentNullException(nameof(secretVault)),
                options ?? throw new ArgumentNullException(nameof(options)),
                networkConnector),
            options)
    {
    }

    private FtpFileProvider(
        FluentFtpSessionFactory sessions,
        FtpFileProviderOptions options)
        : this(sessions, options, sessions)
    {
    }

    internal FtpFileProvider(
        IRemoteHierarchicalFileSessionFactory sessions,
        FtpFileProviderOptions options,
        IFtpFeatureSource? featureSource = null)
        : base(
            sessions,
            options.ProfileId,
            options.Authority,
            options.RemoteRoot,
            protocolName: "FTP",
            allowBackslashSegments: false,
            FileNameComparison.ProviderDefined,
            options.ReconnectPolicy,
            ProviderLimits,
            options.CanEncodeName)
    {
        Options = options;
        _featureSource = featureSource;
        Diagnostics = options.TransportSecurity == FtpTransportSecurity.Plaintext
            ? [new FtpProviderDiagnostic(
                "ftp_plaintext_transport",
                "FTP control, credentials, file names, and file contents are transmitted without TLS.")]
            : [];
    }

    public FtpFileProviderOptions Options { get; }

    public IReadOnlyList<FtpProviderDiagnostic> Diagnostics { get; }

    public FtpConnectionSnapshot? LastConnection => _featureSource?.LastConnection;
}

internal interface IFtpFeatureSource
{
    FtpConnectionSnapshot? LastConnection { get; }
}
