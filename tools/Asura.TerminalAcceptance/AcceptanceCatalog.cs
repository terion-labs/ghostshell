namespace Asura.TerminalAcceptance;

internal static class AcceptanceCatalog
{
    public static IReadOnlyList<AcceptanceCheck> All { get; } =
    [
        new(
            "named-interactive-host",
            "Named physical or self-hosted interactive desktop",
            "Confirm this is the declared system and an interactive, non-headless desktop with a real window manager/compositor, keyboard, pointing device, and installed IME. Use BLOCKED for a container, Xvfb, unattended CI, or when remote input changes what is being tested.",
            "Confirm the Windows 11 edition/build and whether the session is local or remote.",
            "Confirm the distribution/version, X11 desktop/window manager, and that this is not Wayland/XWayland."),
        new(
            "packaged-real-pty-backend",
            "Exact packaged build, managed renderer, and real PTY",
            "Open a local terminal in the package started by this runner. Confirm its status reports libghostty-vt with an Avalonia renderer and portable PTY, live input works, and the shell is attached to a real pseudo-terminal. Record the status text and command outcome, never the shell history or user path.",
            "Run `mode con` in cmd.exe or an equivalent ConPTY-sensitive probe and record only whether it succeeded.",
            "Run `test -t 0 && tty`; record only the TTY result and `/dev/pts/<redacted>` rather than the device number."),
        new(
            "interactive-tui",
            "Interactive full-screen TUI",
            "Run an installed full-screen TUI such as vim, less, htop, OpenCode, or Codex. Exercise navigation, editing, redraw, color, an interactive confirmation, and clean exit back to the shell. Record the TUI and version.",
            "Include function, navigation, Ctrl, Alt, and Shift-modified keys.",
            "Include function, navigation, Ctrl, Alt, and Shift-modified keys under X11."),
        new(
            "unicode-cell-fidelity",
            "Unicode glyph fallback and terminal-cell fidelity",
            "Render Ukrainian, Japanese, emoji, a combining accent, and double-width characters. Verify glyph fallback, combining placement, cursor movement, selection, wrapping, and cell widths visually.",
            "Use the packaged Windows fonts and test at the active display scale.",
            "Record the selected monospace/CJK/emoji font families and test at the active X11 scale."),
        new(
            "ime-composition",
            "IME preedit, candidates, and committed text",
            "Use an installed IME to compose text in the live terminal. Verify preedit text, candidate-window placement at the cursor, cancellation, selection, committed Unicode, and subsequent cursor/cell alignment. Unicode paste is not an IME pass.",
            "Record the Windows input method name and keyboard layout without recording composed personal text.",
            "Record the Linux input framework and input method name without recording composed personal text."),
        new(
            "resize-grid",
            "Continuous resize and PTY grid synchronization",
            "Continuously resize the main and Quick Terminal windows while the TUI redraws. Verify intermediate output remains coherent, rows/columns reach the child PTY, and the final cursor/grid is exact.",
            "Check maximized, restored, and at least two Windows display-scale or monitor placements where available.",
            "Check maximized, restored, and at least two X11 sizes; use `stty size` to confirm the final grid."),
        new(
            "mouse-reporting",
            "Terminal mouse reporting and ordinary selection",
            "With terminal mouse mode enabled, verify press, release, drag, and wheel reach the TUI. With mouse mode disabled, verify ordinary selection, focus, scrolling, and copy behavior are not sent as terminal mouse reports.",
            "Exercise the physical pointer or touchpad through the Windows desktop.",
            "Exercise the physical pointer or touchpad through the X11 desktop."),
        new(
            "clipboard-safety",
            "Clipboard copy, paste, and fail-closed policy",
            "Copy terminal text out, paste single-line and multiline Unicode in, cancel and confirm guarded control-character paste, and verify brokerless OSC 52 read/write attempts fail closed. Use synthetic non-secret text and do not put clipboard contents in evidence notes.",
            "Exercise Windows clipboard shortcuts and the terminal context path.",
            "Exercise X11 clipboard shortcuts and the terminal context path."),
        new(
            "alternate-screen",
            "Alternate-screen entry, redraw, and restoration",
            "Enter and exit an alternate-screen TUI repeatedly. Verify alternate content replaces the primary view, resize/redraw works while active, and primary scrollback plus cursor restore after every exit.",
            "Repeat once after changing Windows focus to another application.",
            "Repeat once after changing X11 focus to another application."),
        new(
            "quick-terminal",
            "OS-global Quick Terminal focus and restore policy",
            "Trigger Quick Terminal from Asura, another application, and the desktop. Verify one toggle per press, terminal focus, transient Escape dismissal, restoration, shortcut re-registration, and useful conflict reporting.",
            "Use the configured RegisterHotKey-backed shortcut.",
            "Use the configured XGrabKey-backed shortcut in the real X11 session."),
        new(
            "sleep-wake",
            "Host sleep and wake recovery",
            "Leave a local PTY and active TUI open, put the named host to sleep, then wake it. Verify honest session state, input, redraw, resize, focus, clipboard, and Quick Terminal recovery. Use BLOCKED if policy or hardware prevents a real suspend/resume.",
            "Use Windows sleep, not merely screen lock or Remote Desktop disconnect.",
            "Use system suspend, not merely display blanking, process stop, or container pause."),
        new(
            "pty-lifecycle",
            "PTY lifecycle, close confirmation, and process cleanup",
            "Exercise clean shell exit, terminal close with active work, cancel/confirm close, repeated open/close, Quick Terminal hide/show, and normal application exit. Verify no Asura-owned shell, PTY, shortcut registration, or application process remains. Exit the package started by this runner before marking PASS.",
            "Confirm no Asura or owned shell process remains in Task Manager or `Get-Process`.",
            "Confirm no Asura or owned shell process remains with `ps` and that the X11 key grab is released."),
    ];
}
