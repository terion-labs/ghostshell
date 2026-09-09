# ADR 0015: Durable Quick Terminal behavior and runtime fallback

- Status: Accepted
- Date: 2026-07-22
- Terminal-view update: [ADR 0040](0040-cross-platform-libghostty-vt-terminal.md)
  supersedes the former native-renderer wording; Quick Terminal uses the same
  Avalonia-managed terminal presentation as the main workspace.

## Context

Quick Terminal is a process-wide desktop surface, but its hotkey, display placement, size, visual treatment, motion, focus dismissal, and session reuse are user choices that must survive restart. Keeping these choices in Avalonia window state would bypass SQLite export/import, make headless inspection impossible, and split the behavior from the durable definition model used by the rest of Asura.

Global shortcut registration can also fail independently of persistence because another application owns the gesture, the platform adapter rejects it, or the desktop backend does not support global shortcuts. A failed custom shortcut must not silently look active or unnecessarily make a previously working default shortcut inaccessible.

## Decision

`QuickTerminalSettings` is a schema-versioned durable definition stored by the common SQLite definition repository and closed `KnownDefinitionRegistry`. The catalog seeds one named default profile with Command + grave, the display containing the main Asura window, 55% height, 82% background opacity, compositor blur requested at 24 px, slide animation, session reuse, and focus-loss dismissal. Background opacity applies only to the default terminal background and Quick Terminal chrome; glyphs, cursor, selection, inverse video, and explicit ANSI backgrounds stay opaque.

The desktop controller observes catalog changes and applies them on the Avalonia UI thread. Height is resolved from the selected monitor's working area, so the payload is independent of scale and pixel dimensions. Explicit reduced motion disables slide animation. Focus-loss and reuse choices are enforced by the window/controller: when reuse is disabled, hiding detaches the renderer attachment, closes the session through the session host, and creates a fresh session on the next opening.

Shortcut registration is diagnostic state, not inferred from the saved value. The UI shows the configured gesture and the actual registration result. If a non-default configured gesture fails, the controller attempts Command + grave as an explicit fallback and reports which gesture, if any, remains active. Unsupported desktops continue to report a typed unavailable state.

The composition root selects Carbon on macOS, a dedicated `RegisterHotKey` message loop on Windows,
and an isolated `XGrabKey` connection on X11. Windows preserves Win32 conflict/error codes at the
adapter boundary. X11 grabs Caps Lock and Num Lock variants and maps `BadAccess` to a conflict.
Wayland is explicitly unavailable: an X11 grab through XWayland is not compositor-global, and no
portal implementation is claimed until its registration and activation lifecycle is verified across
the supported compositors. Escape uses a separate transient registration only while Quick Terminal
is active; its callback follows the same pending-paste cancellation path as a window Escape key.

Blur radius is durable intent. Avalonia exposes compositor blur as a capability hint rather than a portable numeric radius. macOS applies the stored radius through the native window compositor when that API is available; other backends request `AcrylicBlur`, then `Blur`, and finally transparent rendering as ordered fallbacks. Reduced-transparency mode disables blur and forces an opaque background.

The native Quick Terminal window is placed once at its final rectangle inside the selected monitor's working area. `MainWindow` follows the display containing Asura's main window, `Primary` uses the operating system's designated primary display, and `ActiveWindow` resolves the foreground application's window before Quick Terminal is shown or activated. The desktop adapter reads only global window bounds; when the host cannot expose foreign-window geometry, placement falls back to the Asura window and then the primary display. It is never animated through global desktop coordinates. A clipped reveal viewport masks an inner panel whose composition translation moves between `-height` and zero. The controller owns the explicit hidden/showing/visible/hiding lifecycle and one cancellable completion deadline, while the compositor owns interpolation. Reversing a transition continues from its current normalized reveal progress. This prevents a lower display's hidden position from appearing on a vertically adjacent display and avoids per-frame native window movement.

## Consequences

- Quick Terminal behavior survives application restart and participates in guarded definition export/import.
- Settings changes update registration, placement, sizing, background opacity, blur intent, motion, focus loss, and hidden-session reuse without restarting Asura.
- Terminal content stays fully legible over translucent backgrounds, and explicit terminal colors preserve their canonical opaque semantics.
- Slide motion is clipped to the selected display's fixed native window and remains reversible while in flight.
- Meta/Command + grave remains the safe platform-mapped fallback when a custom gesture is invalid or conflicts and the default remains available.
- Windows and real X11 desktops expose typed shortcut conflicts; Wayland and headless Linux sessions expose typed unsupported diagnostics.
- Monitor placement supports the main Asura window, the operating system's primary display, and the foreground window of any application. macOS, Windows, and X11 provide native bounds adapters; Wayland falls back because it has no portable foreign-window geometry protocol.
- Session reuse is implemented across hide/show cycles. Restoring a Quick Terminal process after an application crash remains part of the broader runtime-snapshot recovery work.
- System-wide reduced-motion detection is not yet exposed by Avalonia as a cross-platform application port; the explicit reduced-motion setting always wins today and can later be ORed with host accessibility state.

## Alternatives rejected

- Storing values only in XAML or process memory would lose them on restart and exclude future CLI/ACP clients.
- Treating a persisted hotkey as proof of registration would hide conflicts and unsupported backends.
- Silently replacing a failed custom gesture in SQLite with the default would destroy user intent and prevent guided recovery.
- Adding platform pixel coordinates to the durable payload would make the settings invalid when monitors or display scale change.
