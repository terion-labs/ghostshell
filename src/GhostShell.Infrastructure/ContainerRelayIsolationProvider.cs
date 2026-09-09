using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure;

/// <summary>
/// Ephemeral relay containers only. No host mounts, published ports or Docker
/// network, and no automatic adoption of a stopped/replaced engine or route.
/// </summary>
public sealed class ContainerRelayIsolationProvider : IWorkspaceConnectionServiceProvider
{
    private readonly Func<CancellationToken, Task<RelayContainerEngine>> _discoverEngine;
    private readonly Func<string, CancellationToken, Task<string>> _prepareArchive;
    private readonly ConcurrentDictionary<Guid, OwnedRelay> _relays = new();
    private readonly IWorkspaceIsolationCommandRunner _runner = new WorkspaceIsolationCommandRunner();
    private const string Helper = "/opt/ghostshell/ghostshell-relay-gateway";

    public ContainerRelayIsolationProvider(IConnectionExecutableLocator locator, Func<string, CancellationToken, Task<string>> prepareArchive)
        : this(token => RelayContainerEngine.DiscoverAsync(locator, new WorkspaceIsolationCommandRunner(), token), prepareArchive) { }

    internal ContainerRelayIsolationProvider(Func<CancellationToken, Task<RelayContainerEngine>> discoverEngine,
        Func<string, CancellationToken, Task<string>> prepareArchive)
    {
        _discoverEngine = discoverEngine;
        _prepareArchive = prepareArchive;
    }
    internal const string ImageRecipe = """
        FROM docker.io/library/ubuntu@sha256:33ceb71981b602c1a7443a53469e4dba065f7503eab3078a2d7a57a2ab987517
        RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends ca-certificates curl iproute2 nftables procps libicu74 libgssapi-krb5-2 libssl3t64 zlib1g && rm -rf /var/lib/apt/lists/*
        COPY payload.tar.gz /tmp/payload.tar.gz
        COPY resolver.conf /etc/resolv.conf
        RUN mkdir -p /opt/ghostshell /home/ghostshell && tar --no-same-owner -xzf /tmp/payload.tar.gz -C /opt/ghostshell && cd /opt/ghostshell && sha256sum -c MANIFEST.sha256 && chmod 755 ghostshell-relay-gateway && chown 1000:1000 /home/ghostshell && rm /tmp/payload.tar.gz
        ENV HOME=/home/ghostshell
        WORKDIR /home/ghostshell
        CMD ["sleep", "infinity"]
        """;

    public WorkspaceIsolationProviderDescriptor Descriptor { get; } = new(new("container-relay"), "Container relay",
        WorkspaceIsolationCapability.DedicatedNetworkNamespace | WorkspaceIsolationCapability.StructuredProcessExecution);

    public async ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> PrepareAsync(
        WorkspaceIsolationPrepareRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mounts.Count != 0 || !request.WorkspaceId.Value.StartsWith("service-", StringComparison.Ordinal))
        {
            throw new ArgumentException("Container relays cannot host interactive workspaces or mount host directories.", nameof(request));
        }
        var engine = await _discoverEngine(cancellationToken).ConfigureAwait(false);
        var archive = await _prepareArchive(engine.Architecture, cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid();
        var name = $"ghostshell-relay-{id:N}";
        var directory = Path.Combine("/tmp", $"gs-relay-{id:N}");
        if (OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Container relays currently require a Unix host."); }
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string image;
        try { image = await PrepareImageAsync(engine, archive, directory, cancellationToken).ConfigureAwait(false); }
        catch { Directory.Delete(directory, recursive: true); throw; }
        var binding = new WorkspaceIsolationBinding(request.WorkspaceId, Descriptor.Id, Descriptor.Capabilities, name, [], id,
            runtimeImageReference: image, network: new(name, "100.64.0.1", "100.64.0.0/30",
                packetSocketPath: Path.Combine(directory, "packets.sock"), relayAttachment: new(engine.Launch(
                    ["exec", "--interactive", "--user", "0", name, Helper, "relay-tap"]), engine.Architecture)));
        var owned = new OwnedRelay(binding, engine, directory);
        _relays[id] = owned;
        try
        {
            await CheckedAsync(engine, ["create", "--name", name, "--label", "com.ghostshell.relay=" + id.ToString("N"),
                "--network", "none", .. (engine.Kind is "docker" ? new[] { "--read-only", "--dns", "198.18.0.0" }
                    // Rootless Podman on SELinux cannot relabel the shared TUN
                    // device. Disable labeling only for this mount-free relay,
                    // never machine-wide; retain user namespaces and seccomp.
                    : ["--security-opt", "label=disable"]),
                "--cap-drop", "ALL", "--cap-add", "NET_ADMIN", "--cap-add", "CHOWN",
                "--security-opt", "no-new-privileges", "--device", "/dev/net/tun",
                "--memory", "1g", "--pids-limit", "128", "--restart", "no",
                "--tmpfs", "/tmp:rw,nosuid,nodev,mode=1777,size=256m", "--tmpfs", "/home/ghostshell:rw,exec,nosuid,nodev,mode=0700,size=768m",
                "--sysctl", "net.ipv4.conf.all.route_localnet=1", image], cancellationToken).ConfigureAwait(false);
            await CheckedAsync(engine, ["start", name], cancellationToken).ConfigureAwait(false);
            await CheckedAsync(engine, ["exec", "--user", "0", name, "/bin/chown", "1000:1000", "/home/ghostshell"], cancellationToken).ConfigureAwait(false);
            if (engine.Kind is "podman")
            {
                // Podman rejects even --dns=none with network=none. Its generated
                // resolver must be updated before any worker or packet route starts.
                // Keep its root-owned overlay writable for this setup only; UID
                // 1000 workers have no DAC override or privilege escalation.
                await CheckedAsync(engine, ["exec", "--user", "0", name, "/bin/sh", "-c", "printf 'nameserver 198.18.0.0\\n' > /etc/resolv.conf"], cancellationToken).ConfigureAwait(false);
            }
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
        }
        catch (Exception startup)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await StopAsync(binding, timeout.Token).ConfigureAwait(false); }
            catch (Exception cleanup) { throw new WorkspaceConnectionServiceStartException(startup, cleanup, new PendingRelayCleanup(this, binding)); }
            throw;
        }
    }

    private async Task<string> PrepareImageAsync(RelayContainerEngine engine, string archive, string directory, CancellationToken token)
    {
        await using var file = File.OpenRead(archive);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
        var recipeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ImageRecipe)))[..12];
        var tag = $"localhost/ghostshell-relay:{engine.Architecture}-{hash[..24]}-{recipeHash}";
        using var inspection = CancellationTokenSource.CreateLinkedTokenSource(token);
        inspection.CancelAfter(TimeSpan.FromSeconds(30));
        var found = await _runner.RunAsync(engine.Launch(["image", "inspect", "--format", "{{.Id}}", tag]), ReadOnlyMemory<byte>.Empty, inspection.Token).ConfigureAwait(false);
        if (found.ExitCode == 0) { return ValidateImageId(found.StandardOutput); }
        // Provisioning has no user secrets and precedes the network-disabled
        // execution container. Only verified app payload bytes enter the build.
        await File.WriteAllTextAsync(Path.Combine(directory, "Dockerfile"), ImageRecipe, token).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "resolver.conf"), "nameserver 198.18.0.0\n", token).ConfigureAwait(false);
        File.Copy(archive, Path.Combine(directory, "payload.tar.gz"));
        try { await CheckedAsync(engine, ["build", "--platform", engine.Architecture is "x64" ? "linux/amd64" : "linux/arm64", "--tag", tag, directory], token).ConfigureAwait(false); }
        finally { File.Delete(Path.Combine(directory, "payload.tar.gz")); File.Delete(Path.Combine(directory, "Dockerfile")); File.Delete(Path.Combine(directory, "resolver.conf")); }
        return ValidateImageId(await CheckedAsync(engine, ["image", "inspect", "--format", "{{.Id}}", tag], token).ConfigureAwait(false));
    }

    public WorkspaceIsolationResult<WorkspaceProcessLaunch> CreateExecLaunch(WorkspaceIsolationBinding binding, WorkspaceIsolationProcessRequest request)
    {
        var owned = GetOwned(binding);
        if (request.ConnectionKind != ConnectionKind.Local || request.UsesHostCredentialBroker
            || request.Mode.HasFlag(WorkspaceProcessMode.AllocateTerminal))
        {
            return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Fail(WorkspaceIsolationErrorCode.UnsupportedConnectionKind);
        }
        List<string> arguments = ["exec", "--interactive", "--user", "1000:1000", "--workdir", "/home/ghostshell"];
        foreach (var (key, value) in request.Environment) { arguments.AddRange(["--env", $"{key}={value}"]); }
        arguments.AddRange([binding.ResourceName, request.HostExecutable, .. request.Arguments]);
        return WorkspaceIsolationResult<WorkspaceProcessLaunch>.Succeed(owned.Engine.Launch(arguments));
    }

    public async ValueTask ConfigureServiceNetworkingAsync(WorkspaceIsolationBinding binding, CancellationToken cancellationToken)
    {
        var owned = GetOwned(binding);
        // Only this root setup command gets NET_ADMIN. Backend workers use UID
        // 1000 and cannot regain capabilities because no-new-privileges is set.
        const string waitForTap = "set -eu; n=0; until /usr/sbin/ip -6 addr show dev relay0 | grep -q fd00:4753:4e57::2; do n=$((n+1)); test $n -lt 100; sleep .1; done; ";
        await CheckedAsync(owned.Engine, ["exec", "--user", "0", binding.ResourceName, "/bin/sh", "-c",
            waitForTap + WorkspaceSdkIsolationProvider.ServiceNetworkingScript], cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<WorkspaceIsolationResult<WorkspaceIsolationBinding>> StopAsync(WorkspaceIsolationBinding binding, CancellationToken cancellationToken)
    {
        if (!_relays.TryGetValue(binding.LeaseId, out var owned)) { return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding); }
        GetOwned(binding);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = timeout.Token;
        await owned.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_relays.ContainsKey(binding.LeaseId)) { return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding); }
            var removed = await _runner.RunAsync(owned.Engine.Launch(["rm", "--force", binding.ResourceName]), ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            if (removed.ExitCode != 0)
            {
                // A timed-out create may never have made a container, or a prior
                // rm may have succeeded despite losing its reply. Only a successful
                // empty inventory proves absence; daemon errors retain the lease.
                var remaining = await CheckedAsync(owned.Engine, ["ps", "--all", "--filter", "name=" + binding.ResourceName, "--format", "{{.Names}}"], cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(remaining)) { throw new IOException("The owned relay container could not be removed."); }
            }
            Directory.Delete(owned.Directory, recursive: true);
            _relays.TryRemove(binding.LeaseId, out _);
            return WorkspaceIsolationResult<WorkspaceIsolationBinding>.Succeed(binding);
        }
        finally { owned.Gate.Release(); }
    }

    private OwnedRelay GetOwned(WorkspaceIsolationBinding binding) =>
        _relays.TryGetValue(binding.LeaseId, out var owned) && owned.Binding == binding
            ? owned : throw new InvalidOperationException("The relay lease is not owned by this provider.");

    private static string ValidateImageId(string value)
    {
        var id = value.Trim();
        var digest = id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;
        if (digest.Length != 64 || digest.AsSpan().IndexOfAnyExcept("0123456789abcdef") >= 0)
        {
            throw new IOException("The local engine returned an invalid immutable image ID.");
        }
        return id;
    }

    private async Task<string> CheckedAsync(RelayContainerEngine engine, IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(arguments[0] is "build" ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30));
        var result = await _runner.RunAsync(engine.Launch(arguments), ReadOnlyMemory<byte>.Empty, timeout.Token).ConfigureAwait(false);
        if (result.ExitCode != 0) { throw new IOException($"Container relay {arguments[0]} failed (exit {result.ExitCode}). {result.StandardError}"); }
        return result.StandardOutput;
    }

    private sealed record OwnedRelay(WorkspaceIsolationBinding Binding, RelayContainerEngine Engine, string Directory)
    {
        internal SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class PendingRelayCleanup(ContainerRelayIsolationProvider provider, WorkspaceIsolationBinding binding) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await provider.StopAsync(binding, timeout.Token).ConfigureAwait(false);
        }
    }
}
