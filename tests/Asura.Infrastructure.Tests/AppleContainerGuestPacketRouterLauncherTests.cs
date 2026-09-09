using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class AppleContainerGuestPacketRouterLauncherTests
{
    [Fact]
    public async Task Start_cleans_stale_guest_before_launch_and_disposes_guest_inside_container()
    {
        var processes = new RecordingProcessRunner();
        var launcher = new AppleContainerGuestPacketRouterLauncher("/usr/local/bin/container", processes);

        var process = await launcher.StartAsync(
            Binding(),
            new byte[32],
            CancellationToken.None);
        process.Stop();
        await process.DisposeAsync();

        Assert.Equal(2, processes.Commands.Count);
        AssertGuestStop(processes.Commands[0]);
        AssertGuestStop(processes.Commands[1]);
        var launch = Assert.Single(processes.Starts);
        AssertArguments(launch.Arguments, "--pid-file", "/var/lib/asura/network.pid");
        Assert.True(processes.GuestProcess.Disposed);
    }

    [Fact]
    public async Task Start_fails_closed_when_stale_guest_cannot_be_stopped()
    {
        var processes = new RecordingProcessRunner(
            new WorkspaceGatewayCommandResult(1, "PID owner does not match"));
        var launcher = new AppleContainerGuestPacketRouterLauncher("/usr/local/bin/container", processes);

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            await launcher.StartAsync(Binding(), new byte[32], CancellationToken.None));

        Assert.Contains("previous workspace guest router", exception.Message, StringComparison.Ordinal);
        Assert.Empty(processes.Starts);
        AssertGuestStop(Assert.Single(processes.Commands));
    }

    [Fact]
    public async Task Missing_mounted_helper_requires_workspace_restart_without_launching_a_route()
    {
        var processes = new RecordingProcessRunner(new WorkspaceGatewayCommandResult(
            1,
            "Error: failed to find target executable /opt/asura/bin/workspace-gateway "
            + "internalError: private runtime details"));
        var launcher = new AppleContainerGuestPacketRouterLauncher("/usr/local/bin/container", processes);

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
            await launcher.StartAsync(Binding(), new byte[32], CancellationToken.None));

        Assert.Contains("Restart the isolated workspace", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private runtime details", exception.Message, StringComparison.Ordinal);
        Assert.Empty(processes.Starts);
        AssertGuestStop(Assert.Single(processes.Commands));
    }

    [Fact]
    public async Task Invalid_readiness_stops_started_guest_inside_container()
    {
        var processes = new RecordingProcessRunner(readinessLine: "BROKEN");
        var launcher = new AppleContainerGuestPacketRouterLauncher("/usr/local/bin/container", processes);

        _ = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await launcher.StartAsync(Binding(), new byte[32], CancellationToken.None));

        Assert.Equal(2, processes.Commands.Count);
        AssertGuestStop(processes.Commands[0]);
        AssertGuestStop(processes.Commands[1]);
        Assert.True(processes.GuestProcess.Disposed);
    }

    private static WorkspaceIsolationBinding Binding() =>
        new(
            new WorkspaceId("workspace"),
            new WorkspaceIsolationProviderId("apple-container"),
            WorkspaceIsolationCapability.DedicatedNetworkNamespace,
            "asura-workspace",
            [],
            Guid.NewGuid(),
            network: new WorkspaceIsolationNetworkBinding(
                "asura-network",
                "192.168.64.1",
                "192.168.64.0/24",
                "/tmp/asura/network.sock",
                "/run/asura/network.sock",
                "/opt/asura/bin/workspace-gateway"));

    private static void AssertGuestStop(WorkspaceGatewayProcessRequest request)
    {
        Assert.Equal("/usr/local/bin/container", request.Executable);
        Assert.Contains("guest-stop", request.Arguments, StringComparer.Ordinal);
        AssertArguments(request.Arguments, "--socket", "/run/asura/network.sock");
        AssertArguments(request.Arguments, "--pid-file", "/var/lib/asura/network.pid");
        Assert.True(request.StandardInput.IsEmpty);
    }

    private static void AssertArguments(
        IReadOnlyList<string> arguments,
        string name,
        string value)
    {
        var index = arguments.IndexOf(name);
        Assert.True(index >= 0);
        Assert.Equal(value, arguments[index + 1]);
    }

    private sealed class RecordingProcessRunner(
        WorkspaceGatewayCommandResult? commandResult = null,
        string readinessLine = "READY v1") : IWorkspaceGatewayProcessRunner
    {
        private readonly WorkspaceGatewayCommandResult _commandResult =
            commandResult ?? new WorkspaceGatewayCommandResult(0, string.Empty);
        private readonly string _readinessLine = readinessLine;

        public List<WorkspaceGatewayProcessRequest> Starts { get; } = [];

        public List<WorkspaceGatewayProcessRequest> Commands { get; } = [];

        public RecordingProcess GuestProcess { get; } = new();

        public ValueTask<WorkspaceGatewayProcessStart> StartAsync(
            WorkspaceGatewayProcessRequest request,
            TimeSpan readinessTimeout,
            CancellationToken cancellationToken)
        {
            Starts.Add(request);
            return ValueTask.FromResult(new WorkspaceGatewayProcessStart(
                GuestProcess,
                _readinessLine));
        }

        public ValueTask<WorkspaceGatewayCommandResult> RunAsync(
            WorkspaceGatewayProcessRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Commands.Add(request);
            return ValueTask.FromResult(_commandResult);
        }
    }

    private sealed class RecordingProcess : IWorkspaceGatewayProcess
    {
        public bool HasExited => Disposed;

        public bool Disposed { get; private set; }

        public string Diagnostic => string.Empty;

        public event EventHandler? Exited
        {
            add { }
            remove { }
        }

        public void Stop() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
