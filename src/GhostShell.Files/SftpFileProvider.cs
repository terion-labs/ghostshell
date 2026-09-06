using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Files;

/// <summary>SFTP provider using SSH.NET behind a vendor-free filesystem seam.</summary>
public sealed class SftpFileProvider : RemoteHierarchicalFileProvider, IDisposable
{
    private static readonly FileProviderLimits ProviderLimits = new(
        maximumListPageSize: 1_000,
        maximumReadBytes: 64L * 1024 * 1024,
        maximumBufferSize: 1024 * 1024);

    public SftpFileProvider(
        ISecretVault secretVault,
        ISshHostKeyTrustStore knownHosts,
        SftpFileProviderOptions options)
        : this(secretVault, knownHosts, options, connectionRuntime: null)
    {
    }

    internal SftpFileProvider(
        ISecretVault secretVault,
        ISshHostKeyTrustStore knownHosts,
        SftpFileProviderOptions options,
        IConnectionRuntime? connectionRuntime,
        IWorkspaceNetworkConnector? networkConnector = null)
        : this(
            new RetainedRemoteFileSessionFactory(
                new SshNetSftpSessionFactory(
                    secretVault ?? throw new ArgumentNullException(nameof(secretVault)),
                    knownHosts ?? throw new ArgumentNullException(nameof(knownHosts)),
                    options ?? throw new ArgumentNullException(nameof(options)),
                    connectionRuntime,
                    networkConnector: networkConnector)),
            options)
    {
    }

    private readonly IDisposable? _sessionOwner;
    private bool _disposed;

    internal SftpFileProvider(
        IRemoteHierarchicalFileSessionFactory sessions,
        SftpFileProviderOptions options)
        : base(
            sessions,
            options.ProfileId,
            options.Authority,
            options.RemoteRoot,
            protocolName: "SFTP",
            allowBackslashSegments: true,
            FileNameComparison.CaseSensitive,
            options.ReconnectPolicy,
            ProviderLimits,
            additionalNameValidator: null,
            // SFTP carries the mode in the same attribute block as the size,
            // so a listing already knows it and a chmod is one message.
            transportCapabilities: FileProviderCapability.Permissions)
    {
        _sessionOwner = sessions as IDisposable;
        Connection = options.Connection;
        Diagnostics = options.Connection.HostKeyPolicy == SshHostKeyPolicy.InsecureIgnore
            ? [new SftpProviderDiagnostic(
                "sftp_host_key_verification_disabled",
                "SSH host-key verification is disabled for this connection profile.")]
            : [];
    }

    public ConnectionProfile Connection { get; }

    public IReadOnlyList<SftpProviderDiagnostic> Diagnostics { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionOwner?.Dispose();
    }
}
