using System.Text.Json;
using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class WorkspaceSdkIsolationProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ghostshell-sdk-test-{Guid.NewGuid():N}");
    private readonly WorkspaceId _workspace = new("sdk-workspace");
    private readonly RecordingRunner _runner = new();

    public WorkspaceSdkIsolationProviderTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(null)]
    [InlineData(WorkspaceIsolationImages.Default)]
    public async Task Binding_preserves_saved_image_choice_separately_from_resolved_runtime_image(string? image)
    {
        _ = await SeedDiskAsync();
        var provider = Provider();
        var request = new WorkspaceIsolationPrepareRequest(_workspace, imageReference: image);
        var first = Success(await provider.PrepareAsync(request, CancellationToken.None));
        var reused = Success(await provider.PrepareAsync(request, CancellationToken.None));

        // UI intent checks compare the saved nullable choice, not the resolved default.
        Assert.Equal(image, first.ImageReference);
        Assert.Equal(image, reused.ImageReference);
        Assert.Equal(WorkspaceIsolationImages.Default, first.RuntimeImageReference);
        Assert.Equal(WorkspaceIsolationImages.Default, reused.RuntimeImageReference);
        _ = Success(await provider.StopAsync(first, CancellationToken.None));
        _ = Success(await provider.StopAsync(reused, CancellationToken.None));
        var restarted = Success(await provider.PrepareAsync(request, CancellationToken.None));
        Assert.Equal(image, restarted.ImageReference);
        Assert.Equal(WorkspaceIsolationImages.Default, restarted.RuntimeImageReference);
        _ = Success(await provider.StopAsync(restarted, CancellationToken.None));
    }

    [Fact]
    public async Task Signed_bundle_loads_provisioned_guest_boot_images_from_cache()
    {
        _ = await SeedDiskAsync();
        var contents = Path.Combine(_directory, "GhostShell.app", "Contents");
        var runtimeDirectory = Path.Combine("runtimes", "osx-arm64", "workspace-runtime");
        var provider = new WorkspaceSdkIsolationProvider(
            Path.Combine(contents, "MacOS", runtimeDirectory, "workspace-runtime"),
            Path.Combine(_directory, "state"), "/app/workspace-network-gateway", _runner, 501, 20,
            (_, _) => Task.FromResult(Path.Combine(_directory, "boot-cache")));
        var binding = Success(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), CancellationToken.None));
        var serve = Assert.Single(_runner.Starts);
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(serve.Arguments[2], CancellationToken.None));
        Assert.Equal(Path.Combine(_directory, "boot-cache", "kernel.bin"),
            config.RootElement.GetProperty("kernelPath").GetString());
        Assert.Equal(Path.Combine(_directory, "boot-cache", "initfs.ext4"),
            config.RootElement.GetProperty("initfsPath").GetString());
        _ = Success(await provider.StopAsync(binding, CancellationToken.None));
    }

    [Fact]
    public async Task Persistent_disk_boots_with_host_attachment_and_never_starts_guest_router()
    {
        var disk = await SeedDiskAsync();
        var provider = Provider();
        var binding = Success(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), CancellationToken.None));

        Assert.Equal(WorkspaceSdkIsolationProvider.ProviderDescriptor.Id, binding.Provider);
        Assert.NotNull(binding.Network?.HostAttachment);
        Assert.Null(binding.Network.GuestHelperPath);
        Assert.Null(binding.Network.GuestSocketPath);
        var serve = Assert.Single(_runner.Starts);
        Assert.Equal("serve", serve.Arguments[0]);
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(serve.Arguments[2], CancellationToken.None));
        Assert.Equal(disk, config.RootElement.GetProperty("rootfsPath").GetString());
        Assert.Equal(JsonValueKind.Null, config.RootElement.GetProperty("memoryBytes").ValueKind);
        Assert.Equal(Path.GetFullPath("/app/kernel.bin"), config.RootElement.GetProperty("kernelPath").GetString());
        Assert.Equal(Path.GetFullPath("/app/initfs.ext4"), config.RootElement.GetProperty("initfsPath").GetString());
        Assert.Equal("/sbin/init", config.RootElement.GetProperty("initialArguments")[0].GetString());
        Assert.Empty(config.RootElement.GetProperty("mounts").EnumerateArray());
        Assert.DoesNotContain(_runner.Commands, command => command.Executable.EndsWith("/container", StringComparison.Ordinal));
        Assert.All(_runner.Starts.Concat(_runner.Commands), command => Assert.DoesNotContain("guest", command.Arguments, StringComparer.Ordinal));
        _ = Success(await provider.StopAsync(binding, CancellationToken.None));
        Assert.Equal("persistent disk", await File.ReadAllTextAsync(disk, CancellationToken.None));
    }

    [Fact]
    public async Task Shared_leases_start_one_vm_and_stop_only_after_last_release()
    {
        _ = await SeedDiskAsync();
        var provider = Provider();
        var request = new WorkspaceIsolationPrepareRequest(_workspace);
        var bindings = await Task.WhenAll(
            provider.PrepareAsync(request, CancellationToken.None).AsTask(),
            provider.PrepareAsync(request, CancellationToken.None).AsTask());
        var first = Success(bindings[0]);
        var second = Success(bindings[1]);
        Assert.NotEqual(first.LeaseId, second.LeaseId);
        Assert.Single(_runner.Starts);

        _ = Success(await provider.StopAsync(first, CancellationToken.None));
        _ = Success(await provider.StopAsync(first, CancellationToken.None));
        Assert.False(_runner.Processes[0].Disposed);
        _ = Success(await provider.StopAsync(second, CancellationToken.None));
        Assert.True(_runner.Processes[0].Disposed);
        Assert.Single(_runner.Commands, static command => command.Arguments[0] == "stop");
    }

    [Fact]
    public async Task Structured_exec_maps_mounts_identity_and_terminal_without_host_shell_interpolation()
    {
        _ = await SeedDiskAsync();
        var provider = Provider();
        var hostMount = Path.Combine(_directory, "project");
        var binding = Success(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace,
            [new WorkspaceIsolationMount(hostMount, "/work", false)]), CancellationToken.None));
        var launch = Assert.IsType<WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success>(provider.CreateExecLaunch(binding,
            new WorkspaceIsolationProcessRequest(ConnectionKind.Local, "/bin/sh", ["-c", "printf '%s' '$unchanged'"],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = "/host/home", ["LANG"] = "en_US.UTF-8" },
                Path.Combine(hostMount, "subdir")))).Value;
        using var request = JsonDocument.Parse(Convert.FromBase64String(launch.Arguments[^1]));
        var value = request.RootElement;
        Assert.Equal("printf '%s' '$unchanged'", value.GetProperty("arguments")[2].GetString());
        Assert.Equal("/work/subdir", value.GetProperty("workingDirectory").GetString());
        Assert.Equal("/home/ghostshell", value.GetProperty("environment").GetProperty("HOME").GetString());
        Assert.Equal(501u, value.GetProperty("userID").GetUInt32());
        Assert.Equal(20u, value.GetProperty("groupID").GetUInt32());
        Assert.Equal(new uint[] { 20, 999 }, value.GetProperty("supplementaryGroups").EnumerateArray().Select(static item => item.GetUInt32()));
        Assert.Empty(launch.Environment);
        Assert.Null(launch.HostWorkingDirectory);
        _ = Success(await provider.StopAsync(binding, CancellationToken.None));
    }

    [Fact]
    public async Task Bare_guest_commands_keep_structured_arguments_and_resolve_only_in_guest()
    {
        _ = await SeedDiskAsync();
        var provider = Provider();
        var binding = Success(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), CancellationToken.None));
        foreach (var executable in new[] { "git", "stat", "./local-tool" })
        {
            var launch = Assert.IsType<WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success>(provider.CreateExecLaunch(binding,
                new WorkspaceIsolationProcessRequest(ConnectionKind.Local, executable, ["a b", "$(unchanged)", "--option"]))).Value;
            using var payload = JsonDocument.Parse(Convert.FromBase64String(launch.Arguments[^1]));
            Assert.Equal(["/usr/bin/env", "--", executable, "a b", "$(unchanged)", "--option"],
                payload.RootElement.GetProperty("arguments").EnumerateArray().Select(static item => item.GetString()), StringComparer.Ordinal);
        }

        _ = Success(await provider.StopAsync(binding, CancellationToken.None));
    }

    [Fact]
    public async Task Releasing_one_shared_lease_revokes_only_its_exec_authority()
    {
        _ = await SeedDiskAsync();
        var provider = Provider();
        var prepare = new WorkspaceIsolationPrepareRequest(_workspace);
        var first = Success(await provider.PrepareAsync(prepare, CancellationToken.None));
        var second = Success(await provider.PrepareAsync(prepare, CancellationToken.None));
        var command = new WorkspaceIsolationProcessRequest(ConnectionKind.Local, "/bin/true");
        _ = Success(await provider.StopAsync(first, CancellationToken.None));
        Assert.Equal(WorkspaceIsolationErrorCode.RuntimeUnavailable,
            Assert.IsType<WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure>(provider.CreateExecLaunch(first, command)).Error.Code);
        Assert.IsType<WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success>(provider.CreateExecLaunch(second, command));
        _ = Success(await provider.StopAsync(second, CancellationToken.None));
    }

    [Fact]
    public async Task Restart_uses_new_socket_pair_and_old_launch_cannot_target_replacement_vm()
    {
        _ = await SeedDiskAsync();
        var provider = Provider();
        var prepare = new WorkspaceIsolationPrepareRequest(_workspace);
        var old = Success(await provider.PrepareAsync(prepare, CancellationToken.None));
        var command = new WorkspaceIsolationProcessRequest(ConnectionKind.Local, "/bin/true");
        var oldLaunch = Assert.IsType<WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success>(provider.CreateExecLaunch(old, command)).Value;
        _ = Success(await provider.StopAsync(old, CancellationToken.None));
        var current = Success(await provider.PrepareAsync(prepare, CancellationToken.None));
        Assert.NotEqual(old.Network!.HostAttachment!.ControlSocketPath, current.Network!.HostAttachment!.ControlSocketPath, StringComparer.Ordinal);
        Assert.NotEqual(old.Network.HostAttachment.PacketSocketPath, current.Network.HostAttachment.PacketSocketPath, StringComparer.Ordinal);
        Assert.Equal(old.Network.HostAttachment.ControlSocketPath, oldLaunch.Arguments[2]);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(current.Network.HostAttachment.ControlSocketPath) < 104);
        Assert.IsType<WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure>(provider.CreateExecLaunch(old, command));
        _ = Success(await provider.StopAsync(old, CancellationToken.None));
        Assert.False(_runner.Processes[1].Disposed);
        Assert.IsType<WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success>(provider.CreateExecLaunch(current, command));

        var mismatched = new WorkspaceIsolationBinding(current.WorkspaceId, current.Provider, current.Capabilities,
            current.ResourceName, current.Mounts, current.LeaseId, current.ImageReference, network: old.Network);
        Assert.IsType<WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure>(provider.CreateExecLaunch(mismatched, command));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.StopAsync(mismatched, CancellationToken.None).AsTask());

        var mixedPair = new WorkspaceIsolationNetworkBinding(current.Network.ResourceName,
            current.Network.Ipv4Gateway, current.Network.Ipv4Subnet,
            hostAttachment: new WorkspaceHostNetworkAttachment(current.Network.HostAttachment.ControlSocketPath, old.Network.HostAttachment.PacketSocketPath));
        var mixedBinding = new WorkspaceIsolationBinding(current.WorkspaceId, current.Provider, current.Capabilities,
            current.ResourceName, current.Mounts, current.LeaseId, current.ImageReference, network: mixedPair);
        Assert.Throws<ArgumentException>(() => provider.CreateExecLaunch(mixedBinding, command));
        _ = Success(await provider.StopAsync(current, CancellationToken.None));
    }

    [Fact]
    public async Task Failed_startup_does_not_publish_binding_or_remove_disk()
    {
        var disk = await SeedDiskAsync();
        _runner.Readiness = "INVALID";
        var result = await Provider().PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), CancellationToken.None);
        Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(result);
        Assert.True(_runner.Processes[0].Disposed);
        Assert.True(File.Exists(disk));
    }

    private WorkspaceSdkIsolationProvider Provider() => new(
        "/app/workspace-runtime", Path.Combine(_directory, "state"),
        "/app/workspace-network-gateway", _runner, 501, 20, (_, _) => Task.FromResult("/app"));

    private async Task<string> SeedDiskAsync()
    {
        var path = Path.Combine(_directory, "state", AppleContainerWorkspaceIsolationProvider.ResourceName(_workspace));
        Directory.CreateDirectory(path);
        var disk = Path.Combine(path, "rootfs.ext4");
        await File.WriteAllTextAsync(Path.Combine(path, "image.txt"), WorkspaceIsolationImages.Default, CancellationToken.None);
        await File.WriteAllTextAsync(disk, "persistent disk", CancellationToken.None);
        return disk;
    }

    [Theory]
    [InlineData("ubuntu:24.04", "docker.io/library/ubuntu:24.04")]
    [InlineData("company/image:1", "docker.io/company/image:1")]
    [InlineData("localhost:5000/image:1", "localhost:5000/image:1")]
    [InlineData("ghcr.io/company/image@sha256:abcd", "ghcr.io/company/image@sha256:abcd")]
    public void Familiar_image_references_are_qualified_for_the_SDK(string input, string expected) =>
        Assert.Equal(expected, WorkspaceSdkIsolationProvider.FullyQualifiedImage(input));

    [Fact]
    public async Task Failed_prepare_keeps_image_identity_and_rejects_another_image_on_retry()
    {
        _runner.CommandResult = new WorkspaceGatewayCommandResult(1, "failed unpack");
        var provider = Provider();
        _ = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), CancellationToken.None));
        var result = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace, imageReference: "alpine:3.22"), CancellationToken.None));
        Assert.Contains("another image", result.Error.Message, StringComparison.Ordinal);
        Assert.Single(_runner.Commands);
        Assert.Empty(_runner.Starts);
    }

    [Fact]
    public async Task Unmarked_existing_disk_cannot_be_assumed_to_match_requested_image()
    {
        var path = Path.Combine(_directory, "state", AppleContainerWorkspaceIsolationProvider.ResourceName(_workspace));
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "rootfs.ext4"), "unverified", CancellationToken.None);
        var result = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await Provider().PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), CancellationToken.None));
        Assert.Contains("no verified image identity", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(_runner.Starts);
        Assert.Empty(_runner.Commands);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("stop")]
    public async Task Failed_bootstrap_or_stop_never_promotes_staged_disk(string failureStage)
    {
        _runner.Respond = request =>
        {
            if (request.Arguments[0] == "prepare")
            {
                // The native prepare contract atomically publishes only a complete
                // unpack; this fixture models that completed, unprovisioned disk.
                File.WriteAllText(request.Arguments[4], "unpacked image");
                return new WorkspaceGatewayCommandResult(0, "READY v1");
            }

            if (request.Arguments[0] == "stop" && failureStage == "stop")
            {
                return new WorkspaceGatewayCommandResult(1, "could not flush VM");
            }

            if (request.Arguments[0] == "exec" && failureStage == "install")
            {
                return new WorkspaceGatewayCommandResult(1, "interrupted package install");
            }

            return new WorkspaceGatewayCommandResult(0, string.Empty);
        };
        var result = await Provider().PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), CancellationToken.None);
        Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(result);
        var directory = Path.Combine(_directory, "state", AppleContainerWorkspaceIsolationProvider.ResourceName(_workspace));
        Assert.False(File.Exists(Path.Combine(directory, "rootfs.ext4")));
        Assert.True(File.Exists(Path.Combine(directory, "preparing.ext4")));
        Assert.Equal(WorkspaceIsolationImages.Default, await File.ReadAllTextAsync(Path.Combine(directory, "image.txt"), CancellationToken.None));
        Assert.All(_runner.Processes, static process => Assert.True(process.Disposed));
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(_runner.Starts[0].Arguments[2], CancellationToken.None));
        Assert.Empty(config.RootElement.GetProperty("mounts").EnumerateArray());
        Assert.Equal("/bin/sleep", config.RootElement.GetProperty("initialArguments")[0].GetString());
    }

    [Fact]
    public async Task Bootstrap_route_exit_cancels_install_and_never_promotes_disk()
    {
        _runner.Respond = request =>
        {
            if (request.Arguments[0] == "prepare")
            {
                File.WriteAllText(request.Arguments[4], "unpacked image");
            }

            return new WorkspaceGatewayCommandResult(0, string.Empty);
        };
        _runner.CommandStarted = (request, cancellationToken) =>
        {
            if (request.Arguments[0] == "exec")
            {
                _runner.Processes[1].Stop();
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Route exit did not cancel package installation.");
            }
        };
        var result = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await Provider().PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), CancellationToken.None));
        Assert.Contains("bootstrap host route stopped", result.Error.Message, StringComparison.Ordinal);
        var directory = Path.Combine(_directory, "state", AppleContainerWorkspaceIsolationProvider.ResourceName(_workspace));
        Assert.False(File.Exists(Path.Combine(directory, "rootfs.ext4")));
        Assert.True(File.Exists(Path.Combine(directory, "preparing.ext4")));
        Assert.All(_runner.Processes, static process => Assert.True(process.Disposed));
    }

    [Fact]
    public async Task Cancelled_initial_prepare_retains_identity_before_any_disk_is_published()
    {
        using var cancellation = new CancellationTokenSource();
        _runner.Respond = _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not observed.");
        };
        var result = Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Failure>(
            await Provider().PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), cancellation.Token));
        Assert.Equal(WorkspaceIsolationErrorCode.Cancelled, result.Error.Code);
        var directory = Path.Combine(_directory, "state", AppleContainerWorkspaceIsolationProvider.ResourceName(_workspace));
        Assert.False(File.Exists(Path.Combine(directory, "rootfs.ext4")));
        Assert.Equal(WorkspaceIsolationImages.Default, await File.ReadAllTextAsync(Path.Combine(directory, "image.txt"), CancellationToken.None));
    }

    private static WorkspaceIsolationBinding Success(WorkspaceIsolationResult<WorkspaceIsolationBinding> result) =>
        Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success>(result).Value;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class RecordingRunner : IWorkspaceGatewayProcessRunner
    {
        public List<WorkspaceGatewayProcessRequest> Starts { get; } = [];
        public List<WorkspaceGatewayProcessRequest> Commands { get; } = [];
        public List<RecordingProcess> Processes { get; } = [];
        public string Readiness { get; set; } = "READY v1";
        public WorkspaceGatewayCommandResult CommandResult { get; set; } = new(0, string.Empty);
        public Func<WorkspaceGatewayProcessRequest, WorkspaceGatewayCommandResult>? Respond { get; set; }
        public Action<WorkspaceGatewayProcessRequest, CancellationToken>? CommandStarted { get; set; }

        public ValueTask<WorkspaceGatewayProcessStart> StartAsync(WorkspaceGatewayProcessRequest request, TimeSpan readinessTimeout, CancellationToken cancellationToken)
        {
            Starts.Add(request);
            var process = new RecordingProcess();
            Processes.Add(process);
            var ready = request.Executable == "/app/workspace-network-gateway"
                ? "READY v1 families=ipv4,ipv6 protocols=tcp,udp mtu=1280"
                : Readiness;
            return ValueTask.FromResult(new WorkspaceGatewayProcessStart(process, ready));
        }

        public ValueTask<WorkspaceGatewayCommandResult> RunAsync(WorkspaceGatewayProcessRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Commands.Add(request);
            CommandStarted?.Invoke(request, cancellationToken);
            if (Respond is not null)
            {
                return ValueTask.FromResult(Respond(request));
            }

            if (request.Arguments[0] == "exec")
            {
                using var payload = JsonDocument.Parse(Convert.FromBase64String(request.Arguments[^1]));
                if (payload.RootElement.GetProperty("arguments")[0].GetString() == "/usr/bin/id")
                {
                    return ValueTask.FromResult(new WorkspaceGatewayCommandResult(0, "20 999"));
                }
            }

            return ValueTask.FromResult(CommandResult);
        }
    }

    private sealed class RecordingProcess : IWorkspaceGatewayProcess
    {
        public bool Disposed { get; private set; }
        public bool HasExited => Disposed;
        public string Diagnostic => string.Empty;
        public event EventHandler? Exited;
        public void Stop()
        {
            Disposed = true;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public ValueTask DisposeAsync()
        {
            Stop();
            return ValueTask.CompletedTask;
        }
    }
}
