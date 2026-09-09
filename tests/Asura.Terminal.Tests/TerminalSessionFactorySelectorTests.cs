namespace Asura.Terminal.Tests;

public sealed class TerminalSessionFactorySelectorTests
{
    [Fact]
    public void Every_supported_desktop_selects_the_cross_platform_ghostty_vt_engine()
    {
        Assert.IsType<GhosttyVtTerminalSessionFactory>(
            TerminalSessionFactorySelector.Create(TerminalRuntimePlatform.MacOs));
        Assert.IsType<GhosttyVtTerminalSessionFactory>(
            TerminalSessionFactorySelector.Create(TerminalRuntimePlatform.Windows));
        Assert.IsType<GhosttyVtTerminalSessionFactory>(
            TerminalSessionFactorySelector.Create(TerminalRuntimePlatform.Linux));
    }

    [Fact]
    public void Unsupported_platform_is_explicit()
    {
        Assert.Throws<PlatformNotSupportedException>(() =>
            TerminalSessionFactorySelector.Create(TerminalRuntimePlatform.Unsupported));
    }
}
