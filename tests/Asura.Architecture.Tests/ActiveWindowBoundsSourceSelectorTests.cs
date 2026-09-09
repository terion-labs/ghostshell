using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class ActiveWindowBoundsSourceSelectorTests
{
    [Theory]
    [InlineData("x11", ":0", null, true)]
    [InlineData(null, ":99", null, true)]
    [InlineData("wayland", ":0", null, false)]
    [InlineData(null, ":0", "wayland-0", false)]
    [InlineData(null, null, null, false)]
    public void Only_real_x11_sessions_expose_foreign_window_bounds(
        string? sessionType,
        string? x11Display,
        string? waylandDisplay,
        bool expected)
    {
        var session = new LinuxDesktopSession(
            sessionType,
            x11Display,
            waylandDisplay);

        Assert.Equal(expected, ActiveWindowBoundsSourceSelector.IsX11Session(session));
    }
}
