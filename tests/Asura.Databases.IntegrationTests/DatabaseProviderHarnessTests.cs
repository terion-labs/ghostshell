using Docker.DotNet.Models;

namespace Asura.Databases.IntegrationTests;

public sealed class DatabaseProviderHarnessTests
{
    [Fact]
    public void Published_container_ports_are_bound_to_ipv4_loopback()
    {
        var parameters = new CreateContainerParameters
        {
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>(StringComparer.Ordinal)
                {
                    ["5432/tcp"] = [new PortBinding()],
                    ["8123/tcp"] = [new PortBinding(), new PortBinding()],
                },
            },
        };

        ContainerDatabaseProviderCase.BindPublishedPortsToLoopback(parameters);

        Assert.All(
            parameters.HostConfig.PortBindings.Values.SelectMany(bindings => bindings),
            binding => Assert.Equal("127.0.0.1", binding.HostIP));
    }
}
