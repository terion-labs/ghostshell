using Asura.Core;

namespace Asura.App.Tests;

public sealed class QuickTerminalScreenResolverTests
{
    private static readonly object Primary = new();
    private static readonly object Secondary = new();

    [Theory]
    [InlineData(QuickTerminalMonitorPolicy.MainWindow, false)]
    [InlineData(QuickTerminalMonitorPolicy.Primary, true)]
    public void Explicit_policies_resolve_the_requested_screen(
        QuickTerminalMonitorPolicy policy,
        bool expectsPrimary)
    {
        var selected = QuickTerminalScreenResolver.Resolve(
            Secondary,
            Primary,
            activeWindowScreen: null,
            policy: policy);

        Assert.Same(expectsPrimary ? Primary : Secondary, selected);
    }

    [Fact]
    public void Active_window_uses_the_resolved_foreground_screen()
    {
        var selected = QuickTerminalScreenResolver.Resolve(
            Primary,
            Primary,
            Secondary,
            QuickTerminalMonitorPolicy.ActiveWindow);

        Assert.Same(Secondary, selected);
    }

    [Fact]
    public void Unavailable_active_window_falls_back_to_main_then_primary()
    {
        Assert.Same(
            Secondary,
            QuickTerminalScreenResolver.Resolve(
                Secondary,
                Primary,
                activeWindowScreen: null,
                QuickTerminalMonitorPolicy.ActiveWindow));
        Assert.Same(
            Primary,
            QuickTerminalScreenResolver.Resolve(
                mainWindowScreen: null,
                Primary,
                activeWindowScreen: null,
                QuickTerminalMonitorPolicy.ActiveWindow));
    }
}
