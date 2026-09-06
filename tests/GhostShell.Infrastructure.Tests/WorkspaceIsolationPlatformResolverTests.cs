using System.Runtime.InteropServices;
using GhostShell.Application;

namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceIsolationPlatformResolverTests
{
    private readonly WorkspaceIsolationPlatformResolver _resolver = new();

    [Fact]
    public void Apple_silicon_on_mac_os_26_selects_bundled_sdk_runtime()
    {
        var support = Assert.IsType<WorkspaceIsolationPlatformSupport.Available>(
            _resolver.Resolve(
                ConnectionHostPlatform.MacOs,
                Architecture.Arm64,
                new Version(26, 0)));

        Assert.Equal(
            WorkspaceSdkIsolationProvider.ProviderDescriptor,
            support.Adapter.Descriptor);
        Assert.Equal(
            WorkspaceSdkIsolationProvider.ProviderDescriptor.Capabilities,
            support.Adapter.Descriptor.Capabilities);
        Assert.NotEqual(
            WorkspaceIsolationCapability.None,
            support.Adapter.Descriptor.Capabilities
            & WorkspaceIsolationCapability.PersistentRootFileSystem);
        Assert.NotEqual(
            WorkspaceIsolationCapability.None,
            support.Adapter.Descriptor.Capabilities
            & WorkspaceIsolationCapability.DedicatedKernel);
        Assert.NotEqual(
            WorkspaceIsolationCapability.None,
            support.Adapter.Descriptor.Capabilities
            & WorkspaceIsolationCapability.DedicatedNetworkNamespace);
        Assert.Equal("workspace-runtime", support.Adapter.RuntimeExecutableName);
        Assert.Equal("GhostShell workspace runtime", support.Adapter.Installation.RuntimeDisplayName);
        Assert.IsType<WorkspaceSdkIsolationProvider>(support.Adapter.CreateProvider("/app/workspace-runtime"));
    }

    [Fact]
    public void Unsupported_mac_reports_each_unsatisfied_host_requirement()
    {
        var support = Assert.IsType<WorkspaceIsolationPlatformSupport.Unavailable>(
            _resolver.Resolve(
                ConnectionHostPlatform.MacOs,
                Architecture.X64,
                new Version(15, 7)));

        Assert.Equal(
            [
                WorkspaceIsolationPlatformLimitation.AppleSiliconRequired,
                WorkspaceIsolationPlatformLimitation.MacOs26Required,
            ],
            support.Limitations);
    }

    [Fact]
    public void Linux_reports_the_unshipped_kvm_and_host_sharing_dependencies()
    {
        var support = Assert.IsType<WorkspaceIsolationPlatformSupport.Unavailable>(
            _resolver.Resolve(
                ConnectionHostPlatform.Linux,
                Architecture.Arm64,
                new Version(6, 12)));

        Assert.Equal(
            [
                WorkspaceIsolationPlatformLimitation.LinuxBackendNotPackaged,
                WorkspaceIsolationPlatformLimitation.LinuxKvmRequired,
                WorkspaceIsolationPlatformLimitation.LinuxHostSharingRuntimeRequired,
            ],
            support.Limitations);
    }

    [Fact]
    public void Windows_does_not_claim_a_wsl_distribution_is_a_private_vm_or_network()
    {
        var support = Assert.IsType<WorkspaceIsolationPlatformSupport.Unavailable>(
            _resolver.Resolve(
                ConnectionHostPlatform.Windows,
                Architecture.X64,
                new Version(10, 0)));

        Assert.Equal(
            [
                WorkspaceIsolationPlatformLimitation.WslDistributionsShareVirtualMachine,
                WorkspaceIsolationPlatformLimitation.WslDistributionsShareNetworkNamespace,
            ],
            support.Limitations);
    }

    [Fact]
    public void Unavailable_support_rejects_a_non_limitation_sentinel()
    {
        _ = Assert.Throws<ArgumentException>(() =>
            new WorkspaceIsolationPlatformSupport.Unavailable(
                [WorkspaceIsolationPlatformLimitation.None]));
    }
}
