using Asura.Application;
using Asura.Core;

namespace Asura.Terminal.Tests;

[Collection(GhosttyVtTestCollection.Name)]
public sealed class TerminalSessionPortConformanceTests
{
    [Fact]
    public void Cross_platform_terminal_engine_implements_every_application_port()
    {
        var implementation = typeof(TerminalSessionFactorySelector).Assembly
            .GetType("Asura.Terminal.GhosttyVtTerminalSession", throwOnError: true);

        Assert.NotNull(implementation);
        Assert.True(typeof(ITerminalPanelSession).IsAssignableFrom(implementation));
        Assert.True(typeof(ITerminalProcess).IsAssignableFrom(implementation));
        Assert.True(typeof(ITerminalState).IsAssignableFrom(implementation));
        Assert.True(typeof(ITerminalRendererAttachment).IsAssignableFrom(implementation));
        Assert.True(typeof(ITerminalAutomation).IsAssignableFrom(implementation));
    }

    [Fact]
    public async Task Factory_preserves_process_environment_and_connection_identity()
    {
        var connectionId = new ConnectionId("terminal-port-test");
        var launch = new TerminalLaunchRequest(
            "/tmp",
            "/bin/sh",
            environment: new Dictionary<string, string>(StringComparer.Ordinal) { ["LANG"] = "C" },
            connectionId: connectionId);
        _ = GhosttyVtTestRuntime.RequireStagedRuntime();
        var factory = new GhosttyVtTerminalSessionFactory(new FakePortablePtyFactory());

        await using var session = await factory.CreateAsync(
            SessionId.New(),
            launch,
            CancellationToken.None);

        var process = Assert.IsAssignableFrom<ITerminalProcess>(session);
        Assert.Same(launch, process.Launch);
        Assert.Equal(connectionId, process.Launch.ConnectionId);
        Assert.Equal("C", process.Launch.Environment["LANG"]);
    }
}
