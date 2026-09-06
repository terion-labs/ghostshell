using GhostShell.Application;

namespace GhostShell.Infrastructure;

/// <summary>
/// Acquires a host NIC route lease from the SDK runtime. The CLI remains attached to
/// the control socket for the lease lifetime; stopping it revokes the host route.
/// </summary>
internal sealed class WorkspaceHostNetworkRouteLauncher(
    string? runtimeExecutable,
    IWorkspaceGatewayProcessRunner processes) : IWorkspaceGuestPacketRouterLauncher
{
    public async ValueTask<IWorkspaceGatewayProcess> StartAsync(
        WorkspaceIsolationBinding binding,
        ReadOnlyMemory<byte> authenticationKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Network?.HostAttachment is not { } attachment)
        {
            throw new InvalidOperationException("The workspace does not have a host-owned NIC attachment.");
        }

        if (string.IsNullOrWhiteSpace(runtimeExecutable))
        {
            throw new FileNotFoundException("The bundled workspace runtime is unavailable.");
        }

        var started = await processes.StartAsync(
                new WorkspaceGatewayProcessRequest(
                    runtimeExecutable,
                    ["network", "--socket", attachment.ControlSocketPath,
                        "--packet-socket", attachment.PacketSocketPath],
                    authenticationKey),
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(started.ReadinessLine, "READY v1", StringComparison.Ordinal))
        {
            await started.Process.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException("The host NIC route returned an invalid readiness response.");
        }

        return started.Process;
    }
}
