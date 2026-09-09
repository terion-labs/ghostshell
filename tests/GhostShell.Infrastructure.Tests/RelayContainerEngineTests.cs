using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.Infrastructure.Tests;

public sealed class RelayContainerEngineTests
{
    [Theory]
    [InlineData("unix:///var/run/docker.sock", false)]
    [InlineData("unix:///Users/test/.orbstack/run/docker.sock", false)]
    [InlineData("ssh://core@127.0.0.1:54321/run/user/501/podman/podman.sock", true)]
    [InlineData("ssh://core@[::1]:54321/run/podman/podman.sock", true)]
    public void AcceptsOnlyLocalEngineTransports(string endpoint, bool podman) => RelayContainerEngine.ValidateEndpoint(endpoint, podman);

    [Theory]
    [InlineData("tcp://127.0.0.1:2375", false)]
    [InlineData("ssh://core@example.com/run/podman.sock", true)]
    [InlineData("ssh://core:secret@127.0.0.1/run/podman.sock", true)]
    [InlineData("ssh://core@127.0.0.1/run/podman.sock", false)]
    [InlineData("unix://remote/run/docker.sock", false)]
    [InlineData("unix:///run/docker.sock?override=1", false)]
    public void RejectsRemoteOrAmbiguousEndpoints(string endpoint, bool podman) =>
        Assert.Throws<IOException>(() => RelayContainerEngine.ValidateEndpoint(endpoint, podman));

    [Theory]
    [InlineData("amd64", "x64")]
    [InlineData("x86_64", "x64")]
    [InlineData("aarch64", "arm64")]
    [InlineData("arm64", "arm64")]
    public void SelectsPayloadForEngineArchitecture(string input, string expected) => Assert.Equal(expected, RelayContainerEngine.NormalizeArchitecture(input));

    [Fact]
    public void KeepsCapturedEngineEndpointOnEveryLaunch()
    {
        var engine = new RelayContainerEngine("/usr/bin/docker", ["--host", "unix:///private/engine.sock"], "arm64");
        Assert.Equal(["--host", "unix:///private/engine.sock", "exec", "container-id", "/bin/true"], engine.Launch(["exec", "container-id", "/bin/true"]).Arguments);
    }

    [Fact]
    public async Task RelayProviderRejectsWorkspaceMountsBeforeEngineDiscovery()
    {
        var provider = new ContainerRelayIsolationProvider(new PathConnectionExecutableLocator(), (_, _) => throw new InvalidOperationException("Must not download"));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.PrepareAsync(new(new WorkspaceId("workspace")), CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => provider.PrepareAsync(new(new WorkspaceId("service-test"),
            [new WorkspaceIsolationMount("/tmp", "/mnt/test", true)]), CancellationToken.None).AsTask());
    }
}
