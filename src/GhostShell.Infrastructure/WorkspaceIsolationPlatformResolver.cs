using System.Runtime.InteropServices;
using GhostShell.Application;

namespace GhostShell.Infrastructure;

public enum WorkspaceIsolationPlatformLimitation
{
    None = 0,
    UnsupportedPlatform = 1,
    AppleSiliconRequired = 2,
    MacOs26Required = 3,
    LinuxBackendNotPackaged = 4,
    LinuxKvmRequired = 5,
    LinuxHostSharingRuntimeRequired = 6,
    WslDistributionsShareVirtualMachine = 7,
    WslDistributionsShareNetworkNamespace = 8,
}

public sealed record WorkspaceIsolationRuntimeInstallation(
    string RuntimeDisplayName,
    Uri Address,
    string OpenFailureMessage);

public sealed class WorkspaceIsolationPlatformAdapter
{
    private readonly Func<string, string?, IWorkspaceIsolationProvider> _createProvider;

    internal WorkspaceIsolationPlatformAdapter(
        WorkspaceIsolationProviderDescriptor descriptor,
        string runtimeExecutableName,
        WorkspaceIsolationRuntimeInstallation installation,
        Func<string, string?, IWorkspaceIsolationProvider> createProvider)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeExecutableName);
        RuntimeExecutableName = runtimeExecutableName;
        Installation = installation
            ?? throw new ArgumentNullException(nameof(installation));
        _createProvider = createProvider
            ?? throw new ArgumentNullException(nameof(createProvider));
    }

    public WorkspaceIsolationProviderDescriptor Descriptor { get; }

    public string RuntimeExecutableName { get; }

    public WorkspaceIsolationRuntimeInstallation Installation { get; }

    public IWorkspaceIsolationProvider CreateProvider(string runtimeExecutablePath, string? stateRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeExecutablePath);
        return _createProvider(runtimeExecutablePath, stateRoot);
    }
}

public abstract record WorkspaceIsolationPlatformSupport
{
    private WorkspaceIsolationPlatformSupport()
    {
    }

    public sealed record Available(
        WorkspaceIsolationPlatformAdapter Adapter) : WorkspaceIsolationPlatformSupport;

    public sealed record Unavailable : WorkspaceIsolationPlatformSupport
    {
        public Unavailable(IReadOnlyList<WorkspaceIsolationPlatformLimitation> limitations)
        {
            ArgumentNullException.ThrowIfNull(limitations);
            if (limitations.Count == 0
                || limitations.Any(limitation =>
                    limitation == WorkspaceIsolationPlatformLimitation.None
                    || !Enum.IsDefined(limitation)))
            {
                throw new ArgumentException(
                    "Unavailable workspace isolation requires recognized platform limitations.",
                    nameof(limitations));
            }

            Limitations = Array.AsReadOnly(limitations.Distinct().ToArray());
        }

        public IReadOnlyList<WorkspaceIsolationPlatformLimitation> Limitations { get; }
    }
}

public sealed class WorkspaceIsolationPlatformResolver
{
    private static readonly WorkspaceIsolationPlatformAdapter AppleContainer = new(
        WorkspaceSdkIsolationProvider.ProviderDescriptor,
        "workspace-runtime",
        new WorkspaceIsolationRuntimeInstallation(
            "GhostShell workspace runtime",
            new Uri(
                "https://github.com/terion-labs/ghostshell/releases/latest",
                UriKind.Absolute),
            "GhostSHELL could not open the application download page."),
        (executable, stateRoot) => new WorkspaceSdkIsolationProvider(executable, stateRoot));

    public WorkspaceIsolationPlatformSupport ResolveCurrent() =>
        Resolve(
            ConnectionRuntimeOptions.Detect().Platform,
            RuntimeInformation.OSArchitecture,
            Environment.OSVersion.Version);

    public WorkspaceIsolationPlatformSupport Resolve(
        ConnectionHostPlatform platform,
        Architecture architecture,
        Version operatingSystemVersion)
    {
        ArgumentNullException.ThrowIfNull(operatingSystemVersion);
        return platform switch
        {
            ConnectionHostPlatform.MacOs => ResolveMacOs(architecture, operatingSystemVersion),
            ConnectionHostPlatform.Linux => new WorkspaceIsolationPlatformSupport.Unavailable(
            [
                WorkspaceIsolationPlatformLimitation.LinuxBackendNotPackaged,
                WorkspaceIsolationPlatformLimitation.LinuxKvmRequired,
                WorkspaceIsolationPlatformLimitation.LinuxHostSharingRuntimeRequired,
            ]),
            ConnectionHostPlatform.Windows => new WorkspaceIsolationPlatformSupport.Unavailable(
            [
                WorkspaceIsolationPlatformLimitation.WslDistributionsShareVirtualMachine,
                WorkspaceIsolationPlatformLimitation.WslDistributionsShareNetworkNamespace,
            ]),
            ConnectionHostPlatform.Other => new WorkspaceIsolationPlatformSupport.Unavailable(
            [
                WorkspaceIsolationPlatformLimitation.UnsupportedPlatform,
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        };
    }

    private static WorkspaceIsolationPlatformSupport ResolveMacOs(
        Architecture architecture,
        Version operatingSystemVersion)
    {
        var limitations = new List<WorkspaceIsolationPlatformLimitation>();
        if (architecture != Architecture.Arm64)
        {
            limitations.Add(WorkspaceIsolationPlatformLimitation.AppleSiliconRequired);
        }

        if (operatingSystemVersion.Major < 26)
        {
            limitations.Add(WorkspaceIsolationPlatformLimitation.MacOs26Required);
        }

        return limitations.Count == 0
            ? new WorkspaceIsolationPlatformSupport.Available(AppleContainer)
            : new WorkspaceIsolationPlatformSupport.Unavailable(limitations);
    }
}
