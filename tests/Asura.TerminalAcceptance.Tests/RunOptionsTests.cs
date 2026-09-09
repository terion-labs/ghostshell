namespace Asura.TerminalAcceptance.Tests;

public sealed class RunOptionsTests
{
    [Fact]
    public void Parser_requires_named_host_operator_build_and_package()
    {
        var options = RunOptions.Parse(
        [
            "--platform", "LinuxX11",
            "--system-name", "ubuntu-lab-01",
            "--observer", "operator-02",
            "--build-label", "rc-20260723-2",
            "--package", "/opt/asura",
        ]);

        Assert.Equal(TargetPlatform.LinuxX11, options.Platform);
        Assert.Equal("ubuntu-lab-01", options.SystemName);
        Assert.Equal("operator-02", options.Observer);
        Assert.Equal("rc-20260723-2", options.BuildLabel);
        Assert.Equal("/opt/asura", options.PackagePath);
    }

    [Fact]
    public void Parser_rejects_unbounded_or_sensitive_identifier_shapes()
    {
        var exception = Assert.Throws<UsageException>(() => RunOptions.Parse(
        [
            "--platform", "Windows",
            "--system-name", "C:\\Users\\alice",
            "--observer", "operator-02",
            "--build-label", "rc-2",
            "--package", "C:\\release",
        ]));

        Assert.Contains("--system-name", exception.Message, StringComparison.Ordinal);
    }
}
