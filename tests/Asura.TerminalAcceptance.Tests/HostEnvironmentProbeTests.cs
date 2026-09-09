namespace Asura.TerminalAcceptance.Tests;

public sealed class HostEnvironmentProbeTests
{
    [Theory]
    [InlineData(":0", true)]
    [InlineData(":2.1", true)]
    [InlineData("unix:7.0", true)]
    [InlineData("localhost:10.0", false)]
    [InlineData("remote.example:0", false)]
    [InlineData("", false)]
    public void Named_host_x11_acceptance_requires_a_local_display(
        string display,
        bool expected)
    {
        Assert.Equal(expected, HostEnvironmentProbe.IsLocalDisplay(display));
    }

    [Theory]
    [InlineData(":99", "Xvfb", ":99", true)]
    [InlineData(":4.0", "Xwayland", ":4", true)]
    [InlineData("unix:7.0", "Xephyr", ":7", true)]
    [InlineData(":99", "Xorg", ":99", false)]
    [InlineData(":99", "Xvfb", ":98", false)]
    [InlineData("remote.example:0", "Xvfb", ":0", false)]
    public void Unsupported_x_server_must_own_the_active_local_display(
        string display,
        string processName,
        string processDisplayArgument,
        bool expected)
    {
        var result = HostEnvironmentProbe.ProcessOwnsDisplay(
            display,
            processName,
            [processName, processDisplayArgument, "-screen", "0"]);

        Assert.Equal(expected, result);
    }
}
