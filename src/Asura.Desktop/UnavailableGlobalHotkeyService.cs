using Asura.Application;
using Asura.Core;

namespace Asura.Desktop;

internal enum GlobalHotkeyUnavailableReason
{
    UnsupportedPlatform,
    Wayland,
    MissingX11Display,
}

internal sealed class UnavailableGlobalHotkeyService : IGlobalHotkeyService
{
    private readonly GlobalHotkeyUnavailableReason _reason;

    public UnavailableGlobalHotkeyService(
        GlobalHotkeyUnavailableReason reason = GlobalHotkeyUnavailableReason.UnsupportedPlatform)
    {
        _reason = reason;
    }

    public event EventHandler? Pressed
    {
        add { }
        remove { }
    }

    public event EventHandler? EscapePressed
    {
        add { }
        remove { }
    }

    public KeyStroke? RegisteredGesture => null;

    public GlobalHotkeyRegistrationResult Register(KeyStroke gesture)
    {
        _ = gesture;
        return Failure(escapeCapture: false);
    }

    public void Unregister()
    {
    }

    public GlobalHotkeyRegistrationResult BeginEscapeCapture() => Failure(escapeCapture: true);

    public void EndEscapeCapture()
    {
    }

    public void Dispose()
    {
    }

    private GlobalHotkeyRegistrationResult Failure(bool escapeCapture)
    {
        var (stableCode, message) = (_reason, escapeCapture) switch
        {
            (GlobalHotkeyUnavailableReason.Wayland, false) => (
                "global_hotkey_wayland_unsupported",
                "Global Quick Terminal shortcuts require an X11 session; this Wayland compositor does not expose a safe global-shortcut protocol to Asura."),
            (GlobalHotkeyUnavailableReason.Wayland, true) => (
                "escape_capture_wayland_unsupported",
                "Transient Escape capture is unavailable on Wayland."),
            (GlobalHotkeyUnavailableReason.MissingX11Display, false) => (
                "global_hotkey_x11_display_unavailable",
                "Global Quick Terminal shortcuts require an accessible X11 DISPLAY."),
            (GlobalHotkeyUnavailableReason.MissingX11Display, true) => (
                "escape_capture_x11_display_unavailable",
                "Transient Escape capture requires an accessible X11 DISPLAY."),
            (_, false) => (
                "global_hotkey_unsupported",
                "Global Quick Terminal shortcuts are not available on this desktop backend."),
            (_, true) => (
                "escape_capture_unsupported",
                "Escape capture is not available on this desktop backend."),
        };

        return new GlobalHotkeyRegistrationResult.Failure(new(
            GlobalHotkeyRegistrationErrorCode.Unsupported,
            stableCode,
            message));
    }
}
