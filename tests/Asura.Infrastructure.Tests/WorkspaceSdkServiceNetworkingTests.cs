using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class WorkspaceSdkServiceNetworkingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("asura-service-network-test-").FullName;
    private readonly WorkspaceId _workspace = new("service-network-test");
    private readonly RecordingRunner _runner = new();

    [Fact]
    public async Task Only_service_provider_can_run_fixed_root_network_setup()
    {
        var provider = await ProviderAsync(service: true);
        var binding = Prepared(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), CancellationToken.None));

        await provider.ConfigureServiceNetworkingAsync(binding, CancellationToken.None);

        var command = _runner.Commands[^1];
        Assert.Equal(binding.Network!.HostAttachment!.ControlSocketPath, command.Arguments[2]);
        using var request = JsonDocument.Parse(Convert.FromBase64String(command.Arguments[^1]));
        Assert.Equal(0u, request.RootElement.GetProperty("userID").GetUInt32());
        Assert.Equal(0u, request.RootElement.GetProperty("groupID").GetUInt32());
        var arguments = request.RootElement.GetProperty("arguments");
        Assert.Equal(WorkspaceSdkIsolationProvider.ServiceNetworkingScript,
            arguments[arguments.GetArrayLength() - 1].GetString());
        Assert.Equal("/usr/bin/systemd-run", request.RootElement.GetProperty("arguments")[0].GetString());
        Assert.Contains("240.0.0.0/8", WorkspaceSdkIsolationProvider.ServiceNetworkingScript, StringComparison.Ordinal);
        Assert.Contains("fd00:4753:4c4f::1", WorkspaceSdkIsolationProvider.ServiceNetworkingScript, StringComparison.Ordinal);
        _ = Prepared(await provider.StopAsync(binding, CancellationToken.None));
    }

    [Fact]
    public async Task Ordinary_workspace_provider_cannot_reinterpret_guest_loopback()
    {
        var provider = await ProviderAsync(service: false);
        var binding = Prepared(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), CancellationToken.None));
        var before = _runner.Commands.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ConfigureServiceNetworkingAsync(binding, CancellationToken.None).AsTask());

        Assert.Equal(before, _runner.Commands.Count);
        _ = Prepared(await provider.StopAsync(binding, CancellationToken.None));
    }

    [Fact]
    public async Task Released_lease_cannot_change_replacement_guest_network()
    {
        var provider = await ProviderAsync(service: true);
        var request = new WorkspaceIsolationPrepareRequest(_workspace);
        var old = Prepared(await provider.PrepareAsync(request, CancellationToken.None));
        _ = Prepared(await provider.StopAsync(old, CancellationToken.None));
        _ = await ProviderAsync(service: true); // The service provider deletes its owned ephemeral disk on stop.
        var current = Prepared(await provider.PrepareAsync(request, CancellationToken.None));
        var before = _runner.Commands.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ConfigureServiceNetworkingAsync(old, CancellationToken.None).AsTask());

        Assert.Equal(before, _runner.Commands.Count);
        _ = Prepared(await provider.StopAsync(current, CancellationToken.None));
    }

    [Fact]
    public async Task Configuration_failure_is_not_reported_as_a_ready_service_route()
    {
        var provider = await ProviderAsync(service: true);
        var binding = Prepared(await provider.PrepareAsync(new WorkspaceIsolationPrepareRequest(_workspace), CancellationToken.None));
        _runner.Fail = true;

        await Assert.ThrowsAsync<IOException>(() => provider.ConfigureServiceNetworkingAsync(binding, CancellationToken.None).AsTask());

        _runner.Fail = false;
        _ = Prepared(await provider.StopAsync(binding, CancellationToken.None));
    }

    private async Task<WorkspaceSdkIsolationProvider> ProviderAsync(bool service)
    {
        var state = Path.Combine(_directory, "state");
        var disk = Path.Combine(state, AppleContainerWorkspaceIsolationProvider.ResourceName(_workspace));
        Directory.CreateDirectory(disk);
        await File.WriteAllTextAsync(Path.Combine(disk, "image.txt"), WorkspaceIsolationImages.Default, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(disk, "rootfs.ext4"), "fixture", CancellationToken.None);
        return new WorkspaceSdkIsolationProvider("/app/workspace-runtime", state, "/app/gateway", _runner,
            501, 20, (_, _) => Task.FromResult("/app"), serviceIsolate: service);
    }

    private static WorkspaceIsolationBinding Prepared(WorkspaceIsolationResult<WorkspaceIsolationBinding> result) =>
        Assert.IsType<WorkspaceIsolationResult<WorkspaceIsolationBinding>.Success>(result).Value;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class RecordingRunner : IWorkspaceGatewayProcessRunner
    {
        public List<WorkspaceGatewayProcessRequest> Commands { get; } = [];
        public bool Fail { get; set; }
        public ValueTask<WorkspaceGatewayProcessStart> StartAsync(WorkspaceGatewayProcessRequest request,
            TimeSpan readinessTimeout, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new WorkspaceGatewayProcessStart(new RecordingProcess(), "READY v1"));

        public ValueTask<WorkspaceGatewayCommandResult> RunAsync(WorkspaceGatewayProcessRequest request,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Commands.Add(request);
            if (request.Arguments[0] == "exec")
            {
                using var payload = JsonDocument.Parse(Convert.FromBase64String(request.Arguments[^1]));
                if (payload.RootElement.GetProperty("arguments")[0].GetString() == "/usr/bin/id")
                {
                    return ValueTask.FromResult(new WorkspaceGatewayCommandResult(0, "20"));
                }
            }
            return ValueTask.FromResult(new WorkspaceGatewayCommandResult(Fail ? 1 : 0, string.Empty));
        }
    }

    private sealed class RecordingProcess : IWorkspaceGatewayProcess
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
