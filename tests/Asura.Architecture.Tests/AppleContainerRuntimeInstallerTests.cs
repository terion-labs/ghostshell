using Asura.Desktop;
using Asura.Infrastructure;

namespace Asura.Architecture.Tests;

public sealed class WorkspaceIsolationRuntimeInstallerTests
{
    private static readonly WorkspaceIsolationRuntimeInstallation Installation = new(
        "Example runtime",
        new Uri("https://example.test/runtime/latest"),
        "The runtime page could not open.");

    [Fact]
    public void Installation_opens_Apples_official_latest_release()
    {
        Uri? opened = null;
        var installer = new WorkspaceIsolationRuntimeInstaller(Installation, address =>
        {
            opened = address;
            return true;
        });

        Assert.Equal("Example runtime", installer.RuntimeDisplayName);

        var result = installer.BeginInstallation();

        Assert.True(result.Started);
        Assert.Null(result.Error);
        Assert.Equal(Installation.Address, opened);
    }

    [Fact]
    public void Installation_reports_when_the_system_cannot_open_the_page()
    {
        var installer = new WorkspaceIsolationRuntimeInstaller(Installation, _ => false);

        var result = installer.BeginInstallation();

        Assert.False(result.Started);
        Assert.Contains("could not open", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
