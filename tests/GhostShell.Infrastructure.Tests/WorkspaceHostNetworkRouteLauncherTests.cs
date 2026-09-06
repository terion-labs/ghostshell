using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceHostNetworkRouteLauncherTests
{
    [Fact]
    public async Task Host_nic_lease_streams_key_to_runtime_without_guest_exec()
    {
        var runner = new RecordingRunner();
        var launcher = new WorkspaceHostNetworkRouteLauncher("/app/workspace-runtime", runner);
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        await using var process = await launcher.StartAsync(Binding(), key, CancellationToken.None);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("/app/workspace-runtime", request.Executable);
        Assert.Equal(["network", "--socket", "/private/c.sock", "--packet-socket", "/private/p.sock"], request.Arguments);
        Assert.Equal(key, request.StandardInput.ToArray());
        Assert.DoesNotContain(Convert.ToBase64String(key), request.Arguments, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Bad_readiness_revokes_route_lease()
    {
        var runner = new RecordingRunner { Readiness = "BROKEN" };
        var launcher = new WorkspaceHostNetworkRouteLauncher("/app/workspace-runtime", runner);
        _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await launcher.StartAsync(Binding(), new byte[32], CancellationToken.None));
        Assert.True(runner.Process.HasExited);
    }

    [Fact]
    public async Task Missing_bundled_runtime_cannot_fall_back_to_guest_exec()
    {
        var runner = new RecordingRunner();
        var launcher = new WorkspaceHostNetworkRouteLauncher(null, runner);
        _ = await Assert.ThrowsAsync<FileNotFoundException>(async () => await launcher.StartAsync(Binding(), new byte[32], CancellationToken.None));
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public void Binding_rejects_mixed_host_and_guest_network_ownership() =>
        Assert.Throws<ArgumentException>(() => new WorkspaceIsolationNetworkBinding("network", "100.64.0.1", "100.64.0.0/30",
            "/packet", "/guest/socket", "/guest/helper", new WorkspaceHostNetworkAttachment("/control", "/host/packet")));

    [Fact]
    public void SDK_attachment_does_not_claim_provider_arbitrary_IP_or_larger_physical_MTU()
    {
        var provider = new WorkspacePacketRouteCapabilities(WorkspaceIpAddressFamilies.Ipv4 | WorkspaceIpAddressFamilies.Ipv6,
            WorkspaceIpProtocolCapabilities.All, 1420);
        var effective = BundledWorkspacePacketGatewayBackend.CapabilitiesForAttachment(provider, Binding().Network!);
        Assert.Equal(1280, effective.MaximumPacketSize);
        Assert.False(effective.Protocols.HasFlag(WorkspaceIpProtocolCapabilities.Other));
        Assert.True(effective.Protocols.HasFlag(WorkspaceIpProtocolCapabilities.Tcp));
        Assert.True(effective.Protocols.HasFlag(WorkspaceIpProtocolCapabilities.Udp));
    }

    private static WorkspaceIsolationBinding Binding() => new(new WorkspaceId("workspace"),
        WorkspaceSdkIsolationProvider.ProviderDescriptor.Id, WorkspaceSdkIsolationProvider.ProviderDescriptor.Capabilities,
        "workspace", [], Guid.NewGuid(), network: new WorkspaceIsolationNetworkBinding("network", "100.64.0.1", "100.64.0.0/30",
            hostAttachment: new WorkspaceHostNetworkAttachment("/private/c.sock", "/private/p.sock")));

    private sealed class RecordingRunner : IWorkspaceGatewayProcessRunner
    {
        public List<WorkspaceGatewayProcessRequest> Requests { get; } = [];
        public Lease Process { get; } = new();
        public string Readiness { get; set; } = "READY v1";
        public ValueTask<WorkspaceGatewayProcessStart> StartAsync(WorkspaceGatewayProcessRequest request, TimeSpan readinessTimeout, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(new WorkspaceGatewayProcessStart(Process, Readiness));
        }

        public ValueTask<WorkspaceGatewayCommandResult> RunAsync(WorkspaceGatewayProcessRequest request, TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A route lease does not execute guest commands.");
    }

    private sealed class Lease : IWorkspaceGatewayProcess
    {
        public bool HasExited { get; private set; }
        public string Diagnostic => string.Empty;
        public event EventHandler? Exited;
        public void Stop()
        {
            HasExited = true;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask DisposeAsync()
        {
            Stop();
            return ValueTask.CompletedTask;
        }
    }
}
