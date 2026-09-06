using GhostShell.Application;

namespace GhostShell.Files;

/// <summary>Cross-platform SMB 2/3 provider backed by SMBLibrary.</summary>
public sealed class SmbFileProvider : RemoteHierarchicalFileProvider
{
    private static readonly FileProviderLimits ProviderLimits = new(
        maximumListPageSize: 1_000,
        maximumReadBytes: 64L * 1024 * 1024,
        maximumBufferSize: 1024 * 1024);

    public SmbFileProvider(
        ISecretVault secretVault,
        SmbFileProviderOptions options,
        IWorkspaceNetworkConnector? networkConnector = null)
        : this(
            new SmbLibrarySessionFactory(
                secretVault ?? throw new ArgumentNullException(nameof(secretVault)),
                options ?? throw new ArgumentNullException(nameof(options)),
                networkConnector),
            options)
    {
    }

    internal SmbFileProvider(
        IRemoteHierarchicalFileSessionFactory sessions,
        SmbFileProviderOptions options)
        : base(
            sessions,
            options.ProfileId,
            options.Authority,
            options.RemoteRoot,
            protocolName: "SMB",
            allowBackslashSegments: false,
            FileNameComparison.ProviderDefined,
            options.ReconnectPolicy,
            ProviderLimits)
    {
        Options = options;
        var diagnostics = new List<SmbProviderDiagnostic>();
        if (options.Authentication is SmbAuthentication.Guest)
        {
            diagnostics.Add(new SmbProviderDiagnostic(
                "smb_guest_authentication",
                "The SMB profile uses guest authentication and may have broader server-defined access."));
        }

        diagnostics.Add(new SmbProviderDiagnostic(
            "smb_transport_security_unverified",
            "SMBLibrary negotiates signing and SMB 3 encryption, but does not expose the selected dialect or encryption state; GhostSHELL cannot attest that this share encrypts file data."));
        Diagnostics = [.. diagnostics];
    }

    public SmbFileProviderOptions Options { get; }

    public IReadOnlyList<SmbProviderDiagnostic> Diagnostics { get; }
}
