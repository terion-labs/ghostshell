using Asura.Application;
using Asura.Core;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class GlobalHotkeyServiceSelectorTests
{
    private static readonly LinuxDesktopSession EmptyLinuxSession = new(null, null, null);

    [Fact]
    public void Native_desktop_platforms_select_their_adapter()
    {
        Assert.Equal(
            GlobalHotkeyBackend.MacOs,
            GlobalHotkeyServiceSelector.Select(
                GlobalHotkeyRuntimePlatform.MacOs,
                EmptyLinuxSession));
        Assert.Equal(
            GlobalHotkeyBackend.Windows,
            GlobalHotkeyServiceSelector.Select(
                GlobalHotkeyRuntimePlatform.Windows,
                EmptyLinuxSession));
    }

    [Theory]
    [InlineData("x11", ":0")]
    [InlineData(" X11 ", "localhost:10.0")]
    [InlineData(null, ":99")]
    public void Linux_x11_session_selects_xgrabkey(string? sessionType, string display)
    {
        var selection = GlobalHotkeyServiceSelector.Select(
            GlobalHotkeyRuntimePlatform.Linux,
            new LinuxDesktopSession(sessionType, display, null));

        Assert.Equal(GlobalHotkeyBackend.LinuxX11, selection);
    }

    [Theory]
    [InlineData("wayland", ":0", null)]
    [InlineData("WAYLAND", null, "wayland-0")]
    [InlineData(null, ":0", "wayland-1")]
    public void Linux_wayland_session_is_explicitly_unsupported(
        string? sessionType,
        string? display,
        string? waylandDisplay)
    {
        var selection = GlobalHotkeyServiceSelector.Select(
            GlobalHotkeyRuntimePlatform.Linux,
            new LinuxDesktopSession(sessionType, display, waylandDisplay));

        Assert.Equal(GlobalHotkeyBackend.UnavailableWayland, selection);
    }

    [Fact]
    public void Linux_without_an_x11_display_and_unknown_platform_are_explicit()
    {
        Assert.Equal(
            GlobalHotkeyBackend.UnavailableMissingX11Display,
            GlobalHotkeyServiceSelector.Select(
                GlobalHotkeyRuntimePlatform.Linux,
                EmptyLinuxSession));
        Assert.Equal(
            GlobalHotkeyBackend.UnavailablePlatform,
            GlobalHotkeyServiceSelector.Select(
                GlobalHotkeyRuntimePlatform.Unsupported,
                EmptyLinuxSession));
    }

    [Fact]
    public void Wayland_diagnostic_does_not_claim_global_shortcut_support()
    {
        using var service = new UnavailableGlobalHotkeyService(
            GlobalHotkeyUnavailableReason.Wayland);

        var registration = Assert.IsType<GlobalHotkeyRegistrationResult.Failure>(
            service.Register(new KeyStroke("GRAVE", KeyModifiers.Meta)));
        var escape = Assert.IsType<GlobalHotkeyRegistrationResult.Failure>(
            service.BeginEscapeCapture());

        Assert.Equal(GlobalHotkeyRegistrationErrorCode.Unsupported, registration.Error.Code);
        Assert.Equal("global_hotkey_wayland_unsupported", registration.Error.StableCode);
        Assert.Contains("Wayland", registration.Error.Message, StringComparison.Ordinal);
        Assert.Equal("escape_capture_wayland_unsupported", escape.Error.StableCode);
    }
}
