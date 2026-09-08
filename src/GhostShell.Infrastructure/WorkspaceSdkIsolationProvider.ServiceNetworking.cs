using GhostShell.Application;

namespace GhostShell.Infrastructure;

public sealed partial class WorkspaceSdkIsolationProvider
{
    // Only a mount-free service VM may reinterpret loopback as the remote SSH
    // host. DNAT preserves the application's original hostname/TLS identity;
    // conntrack reverses both translations on replies. The host gateway accepts
    // these reserved aliases only in its service proxy-name resolution mode.
    // IPv6 has no route_localnet equivalent: choose the NIC as the original
    // socket source so reverse SNAT does not deliver an eth0 packet to ::1.
    // Flush only that exact local route; metric 0 is otherwise normalized to
    // 1024 and leaves the kernel's preferred metric-0 loopback route intact.
    internal const string ServiceNetworkingScript = """
        set -eu
        test -x /usr/sbin/nft
        /usr/sbin/sysctl -q -w net.ipv4.conf.all.route_localnet=1
        /usr/sbin/ip -6 route flush table local exact ::1
        /usr/sbin/ip -6 route add local ::1 dev lo table local metric 1 src fd00:4753:4e57::2
        /usr/sbin/nft add table inet ghostshell_service
        /usr/sbin/nft -f - <<'GHOSTSHELL_NFT'
        flush table inet ghostshell_service
        table inet ghostshell_service {
            chain output {
                type nat hook output priority dstnat; policy accept;
                meta l4proto tcp ip daddr 127.0.0.0/8 counter dnat ip prefix to ip daddr map { 127.0.0.0/8 : 240.0.0.0/8 }
                meta l4proto tcp ip6 daddr ::1 counter dnat ip6 to fd00:4753:4c4f::1
            }
            chain postrouting {
                type nat hook postrouting priority srcnat; policy accept;
                meta l4proto tcp ct status dnat ip daddr 240.0.0.0/8 counter masquerade
                meta l4proto tcp ct status dnat ip6 daddr fd00:4753:4c4f::1 counter snat ip6 to fd00:4753:4e57::2
            }
        }
        GHOSTSHELL_NFT
        """;

    public async ValueTask ConfigureServiceNetworkingAsync(
        WorkspaceIsolationBinding binding,
        CancellationToken cancellationToken)
    {
        if (!_serviceIsolate)
        {
            throw new InvalidOperationException("Remote loopback routing is restricted to mount-free service isolates.");
        }

        ValidateBinding(binding);
        if (!_workspaces.TryGetValue(binding.WorkspaceId, out var state))
        {
            throw new InvalidOperationException("The service isolate is not running.");
        }

        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!state.Leases.TryGetValue(binding.LeaseId, out var lease)
                || lease.Attachment != binding.Network!.HostAttachment
                || state.Process is not { HasExited: false })
            {
                throw new InvalidOperationException("The service isolate lease is no longer active.");
            }

            var launch = ExecLaunch(lease.Attachment.ControlSocketPath,
                new WorkspaceSdkExecRequest(["/usr/bin/systemd-run", "--wait", "--pipe", "--collect", "--quiet",
                    "--property=CapabilityBoundingSet=CAP_NET_ADMIN", "--property=NoNewPrivileges=yes",
                    "/bin/sh", "-c", ServiceNetworkingScript], EmptyEnvironment, "/", 0, 0));
            var result = await _processes.RunAsync(
                new WorkspaceGatewayProcessRequest(launch.Executable, launch.Arguments, ReadOnlyMemory<byte>.Empty),
                TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new IOException($"Service isolate loopback routing could not be configured (exit {result.ExitCode}). {result.Diagnostic}");
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }
}
