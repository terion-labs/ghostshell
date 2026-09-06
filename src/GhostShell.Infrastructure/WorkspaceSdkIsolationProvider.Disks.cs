using System.Net;
using System.Security.Cryptography;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

public sealed partial class WorkspaceSdkIsolationProvider
{
    // First creation provisions a disposable, unshared VM. It has an explicitly
    // selected direct host route only for package installation. No user mounts,
    // credentials or existing workspace disk enter this bootstrap environment.
    private const string BootstrapScript = """
        set -eu
        export DEBIAN_FRONTEND=noninteractive
        apt-get update
        dpkg --configure -a || apt-get -f install -y --no-install-recommends
        apt-get install -y --no-install-recommends bash ca-certificates curl dbus git iproute2 iputils-ping less locales man-db openssh-client procps sudo systemd systemd-sysv tzdata vim-tiny wget
        apt-get clean
        rm -rf /var/lib/apt/lists/*
        : > /etc/machine-id
        mkdir -p /var/lib/dbus
        : > /var/lib/dbus/machine-id
        systemctl set-default multi-user.target
        systemctl mask dev-hugepages.mount sys-fs-fuse-connections.mount systemd-update-utmp.service systemd-tmpfiles-setup.service console-getty.service
        test -x /sbin/init
        sync
        """;

    private async ValueTask PrepareDiskAsync(
        WorkspaceIsolationPrepareRequest request,
        string rootfs,
        IProgress<WorkspaceIsolationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pending = Path.Combine(Path.GetDirectoryName(rootfs)!, "preparing.ext4");
        if (!File.Exists(pending))
        {
            var prepared = await _processes.RunAsync(
                    new WorkspaceGatewayProcessRequest(_executable,
                        ["prepare", "--image", FullyQualifiedImage(request.ImageReference ?? WorkspaceIsolationImages.Default),
                            "--rootfs", pending, "--state-directory", Path.Combine(_stateRoot, "images")],
                        ReadOnlyMemory<byte>.Empty),
                    TimeSpan.FromMinutes(15), cancellationToken)
                .ConfigureAwait(false);
            if (prepared.ExitCode != 0 || !File.Exists(pending))
            {
                throw new IOException("The SDK could not unpack the selected workspace image. The existing workspace disk was not changed.");
            }
        }

        if (request.ImageReference is null or WorkspaceIsolationImages.Default)
        {
            progress?.Report(new WorkspaceIsolationProgress("Installing the workspace's base tools through the host gateway…"));
            await BootstrapDiskAsync(request.WorkspaceId, pending, cancellationToken).ConfigureAwait(false);
        }

        File.Move(pending, rootfs, overwrite: false);
    }

    internal static string FullyQualifiedImage(string image)
    {
        // The SDK requires a registry domain; UI image references use ordinary
        // Docker shorthand. A port belongs to a registry only before a slash.
        var slash = image.IndexOf('/');
        if (slash < 0)
        {
            return $"docker.io/library/{image}";
        }

        var first = image[..slash];
        return first.Contains('.', StringComparison.Ordinal) || first.Contains(':', StringComparison.Ordinal)
            || string.Equals(first, "localhost", StringComparison.Ordinal)
            ? image
            : $"docker.io/{image}";
    }

    private async ValueTask BootstrapDiskAsync(WorkspaceId workspaceId, string rootfs, CancellationToken cancellationToken)
    {
        var socketDirectory = SocketDirectory(workspaceId);
        var control = Path.Combine(socketDirectory, "bootstrap.sock");
        var packet = Path.Combine(socketDirectory, "bootstrap-p.sock");
        var configuration = Configuration(new WorkspaceIsolationPrepareRequest(workspaceId), rootfs, control, ["/bin/sleep", "infinity"]);
        await using var runtime = await StartRuntimeAsync(configuration, Path.GetDirectoryName(rootfs)!, cancellationToken).ConfigureAwait(false);
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var ethernet = await _processes.StartAsync(new WorkspaceGatewayProcessRequest(_executable,
                    ["network", "--socket", control, "--packet-socket", packet], key), StartupTimeout, cancellationToken)
                .ConfigureAwait(false);
            await using var ethernetLease = ethernet.Process;
            if (!string.Equals(ethernet.ReadinessLine, "READY v1", StringComparison.Ordinal))
            {
                throw new IOException("The bootstrap host NIC route could not start.");
            }

            var dns = await new SystemWorkspaceGatewayDnsSource().GetHostDnsServersAsync(cancellationToken).ConfigureAwait(false);
            var resolver = dns.FirstOrDefault(static address =>
                    !IPAddress.IsLoopback(address) && !address.IsIPv6LinkLocal)
                ?? IPAddress.Parse("1.1.1.1");
            var host = await _processes.StartAsync(new WorkspaceGatewayProcessRequest(_gatewayExecutable,
                    ["host", "--socket", packet, "--mode", "direct", "--mtu", "1280", "--dns", resolver.ToString()], key),
                    StartupTimeout, cancellationToken)
                .ConfigureAwait(false);
            await using var hostLease = host.Process;
            if (!host.ReadinessLine.StartsWith("READY v1 ", StringComparison.Ordinal))
            {
                throw new IOException("The bootstrap host gateway could not start.");
            }

            var launch = ExecLaunch(control, new WorkspaceSdkExecRequest(
                ["/bin/sh", "-c", BootstrapScript], EmptyEnvironment, "/", 0, 0));
            using var routeLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            void RouteExited(object? sender, EventArgs arguments)
            {
                try
                {
                    routeLifetime.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // An already-dispatched exit event may finish after teardown.
                }
            }
            ethernetLease.Exited += RouteExited;
            hostLease.Exited += RouteExited;
            try
            {
                if (ethernetLease.HasExited || hostLease.HasExited)
                {
                    await routeLifetime.CancelAsync().ConfigureAwait(false);
                }

                var installed = await _processes.RunAsync(new WorkspaceGatewayProcessRequest(
                        launch.Executable, launch.Arguments, ReadOnlyMemory<byte>.Empty), TimeSpan.FromMinutes(15), routeLifetime.Token)
                    .ConfigureAwait(false);
                if (installed.ExitCode != 0)
                {
                    // Bootstrap has neither user mounts nor credentials. Its package
                    // diagnostics are safe to surface and distinguish disk/setup errors
                    // from route loss instead of hiding every failure behind one label.
                    throw new IOException($"The base workspace tools could not be installed (exit {installed.ExitCode}). "
                        + $"Retry creation to resume the staged disk. {installed.Diagnostic}");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && routeLifetime.IsCancellationRequested)
            {
                throw new IOException("The bootstrap host route stopped during package installation. "
                    + $"NIC exit: {ethernetLease.ExitCode}; gateway exit: {hostLease.ExitCode}. "
                    + $"{ethernetLease.Diagnostic} {hostLease.Diagnostic} {runtime.Diagnostic}");
            }
            finally
            {
                ethernetLease.Exited -= RouteExited;
                hostLease.Exited -= RouteExited;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        // Only a successful graceful stop permits promotion. Failed installation
        // leaves a staged disk, never a disk marked ready for user workloads.
        var stopped = await _processes.RunAsync(new WorkspaceGatewayProcessRequest(_executable,
                ["stop", "--socket", control], ReadOnlyMemory<byte>.Empty), TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);
        if (stopped.ExitCode != 0)
        {
            throw new IOException("The staged workspace disk could not be safely stopped; it has not been promoted.");
        }
    }

}
