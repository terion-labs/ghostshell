using Asura.Core;

namespace Asura.Core.Tests;

public sealed class PanelLaunchCapabilitiesTests
{
    [Fact]
    public void Local_and_ssh_connections_declare_shell_file_monitor_docker_and_git_panels()
    {
        var expected = new[]
        {
            PanelKind.Terminal,
            PanelKind.FileViewer,
            PanelKind.Statistics,
            PanelKind.ProcessMonitor,
            PanelKind.Docker,
            PanelKind.Git,
        };

        AssertCapabilities(new ConnectionEndpoint.Local(), PanelKind.Terminal, expected);
        AssertCapabilities(new ConnectionEndpoint.Ssh("example.test"), PanelKind.Terminal, expected);
    }

    [Fact]
    public void Docker_and_wsl_connections_declare_shell_and_monitor_panels()
    {
        var expected = new[]
        {
            PanelKind.Terminal,
            PanelKind.Statistics,
            PanelKind.ProcessMonitor,
        };

        AssertCapabilities(new ConnectionEndpoint.Docker("api"), PanelKind.Terminal, expected);
        AssertCapabilities(new ConnectionEndpoint.Wsl("Ubuntu"), PanelKind.Terminal, expected);
    }

    [Fact]
    public void File_provider_configurations_only_declare_file_viewer()
    {
        IPanelLaunchCapabilitySource source =
            new FileProviderConfiguration.S3("asura-test");

        Assert.Equal(PanelKind.FileViewer, source.PanelLaunchCapabilities.DefaultPanel);
        Assert.Equal(
            [PanelKind.FileViewer],
            source.PanelLaunchCapabilities.SupportedPanels);
    }

    [Fact]
    public void Capability_set_rejects_non_connection_panels_and_missing_default()
    {
        Assert.Throws<ArgumentException>(() =>
            new PanelLaunchCapabilities(PanelKind.Terminal, PanelKind.FileViewer));
        Assert.Throws<ArgumentException>(() =>
            new PanelLaunchCapabilities(PanelKind.Browser, PanelKind.Browser));
        Assert.Throws<ArgumentException>(() =>
            new PanelLaunchCapabilities(PanelKind.Terminal, PanelKind.Terminal, PanelKind.Terminal));
    }

    private static void AssertCapabilities(
        IPanelLaunchCapabilitySource source,
        PanelKind expectedDefault,
        IReadOnlyList<PanelKind> expectedPanels)
    {
        Assert.Equal(expectedDefault, source.PanelLaunchCapabilities.DefaultPanel);
        Assert.Equal(expectedPanels, source.PanelLaunchCapabilities.SupportedPanels);
    }
}
