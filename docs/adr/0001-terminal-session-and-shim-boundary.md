# ADR 0001: Terminal session and native shim boundary

- Status: Superseded by [ADR 0040](0040-cross-platform-libghostty-vt-terminal.md)
- Date: 2026-07-22

## Context

Ghostty's embedding ABI is not stable and its current renderer exposes an Apple native view. The original bootstrap let an Avalonia control own the Ghostty handle, PTY, polling, confirmation dialog, and disposal. That made visual detachment indistinguishable from closing a terminal session and made panel, tab, and window lifecycle impossible to coordinate.

## Decision

`Asura.Application` owns the platform-neutral terminal and panel ports. `Asura.Terminal` implements those ports and privately owns all libghostty types and P/Invoke calls. Presentation code addresses sessions and attachments through `ISessionHostClient`; it never receives a Ghostty handle.

The Asura native ABI remains a small pinned shim. It exposes renderer attach/reparent/detach separately from terminal destruction. Removing or recreating an Avalonia native host detaches only the view and leaves the PTY alive. Explicit panel, tab, window, or session close is the only path that destroys the terminal.

M2 extends that shim with a size-checked, version-1 launch-options entrypoint while retaining the original attach symbol. `TerminalLaunchRequest` snapshots and validates structured argv and environment data before it reaches the terminal adapter. The shim passes environment entries through libghostty's structured surface options. libghostty 1.3.1 exposes an embedded command as a shell string, so the shim serializes every executable/argv word with POSIX single-quote escaping and a bounded allocation; callers never concatenate shell syntax and launch values are never included in errors or session events.

Each native terminal owns a libghostty app/config instance so applying a terminal-profile snapshot cannot restyle sibling terminals. The managed definition, launch snapshot, and native structure carry the same supported settings. A selected terminal-keymap snapshot is translated into reviewed libghostty binding actions and loaded over a fresh libghostty default config; a structured Asura launch does not load user Ghostty files whose `key-remap` or bindings could change the selected shortcuts. The legacy attach path retains its historical user-config behavior. Copy/paste, selection, editing, font size, clear, hosted viewport scrolling, shell control, and native Find therefore follow the selected session snapshot instead of an unrelated global Ghostty config. The embedded AppKit runtime supplies the search field that libghostty's `start_search` action expects from its host application.

The options ABI remains version 1: new profile and top-level keymap fields are appended, the native shim reads each appended field only when `struct_size` includes it, and the original prefix and attach symbol remain valid. The shim validates the historical structure prefix rather than the current `sizeof`, so an older version-1 caller does not have to allocate fields it cannot know about. Existing schema-1 JSON that omits the appended settings loads conservative defaults, so persistence does not require a schema bump or migration.

The settings UI exposes multi-stroke sequences and configurable prefix timing only for the Application layer. Terminal bindings must contain exactly one stroke: validation preserves but blocks saving an imported multi-stroke Terminal binding until it is repaired, the recorder captures one stroke, and the native translator independently rejects an invalid launch snapshot. This keeps selected terminal shortcuts deterministic across the native and managed renderers. Supporting terminal-layer sequences later requires a typed cross-engine timeout/repeat/failure contract rather than silently approximating one engine with the other.

The native host-key interceptor calls managed policy synchronously before libghostty receives a physical key. The reverse P/Invoke entrypoint is a process-lifetime static delegate, and native userdata is an opaque monotonically increasing registration ID. A concurrent registry holds only weak references to registrations; each live session owns its registration and managed handler. Deterministic disposal clears the native callback before unregistering. If a session is abandoned and finalized while native teardown is still queued, a stale native invocation reaches the permanent static thunk, fails to resolve the collected registration, and safely passes the key through. Finalization performs only managed registry cleanup and never synchronously dispatches to the AppKit main thread.

M3 adds a separate versioned physical-input gate covering key down/up,
modifier changes, IME composition and commit, paste, and mouse button/move/scroll
before libghostty receives the event. The session host binds that synchronous
callback to the exact interactive human attachment and reacquires its lease,
preempting any agent lease without transport or asynchronous UI work. Every
accepted physical event also advances a native input epoch. Programmatic text,
key, character-chord, paste, and mouse sends capture that epoch and recheck it
on the AppKit main thread immediately before dispatch, so a send queued before
later human input is rejected instead of overtaking it. Character chords use a
closed semantic libghostty key event rather than AppKit text or active keyboard
layout translation. A missing, stale, future-version, or throwing gate fails
closed.

The implementation is based on the documentation bundled with the pinned libghostty 1.3.1 artifact. Enforcement is deliberately scoped as follows:

| Terminal-profile setting | macOS/libghostty enforcement | Scope and limitation |
|---|---|---|
| Font family, size, and line height | Per-terminal Ghostty config plus surface font size | Applies when a new terminal is created. Ghostty may fall back when the requested family is not installed. |
| Foreground, background, cursor, selection, and 16-color ANSI palette | Per-terminal Ghostty config | Applies to the new terminal only; it does not change application chrome. |
| Cursor shape/blink and scrollback | Per-terminal Ghostty config | Asura expresses scrollback in lines while libghostty accepts bytes, so the adapter budgets 256 bytes per line. Allocation remains lazy. |
| Clipboard read/write and paste safety | Ghostty `ask`/`allow`/`deny` and paste-protection config, backed by native consent alerts for OSC 52 and unsafe paste | Governs the terminal's system-clipboard paths only. Agent `terminal.write` and exact text input do not use the clipboard and remain subject to the agent/input broker instead. Clipboard contents are neither logged nor placed in error text. |
| Link policy | Ghostty URL detection plus shim handling of open-link actions as open, confirm, or consume | Governs links emitted by terminal content only. It does not authorize browser navigation or agent network access. |
| IME | The AppKit `NSTextInputClient` preedit path is enabled or bypassed per terminal | Governs native keyboard composition only. Programmatic exact text input is not IME composition. |
| Shell integration | Ghostty automatic shell-integration config (`detect`, disabled, or a named supported shell) | Injection depends on a compatible shell startup; an explicit non-shell command does not acquire shell semantics. |
| Bell | Ghostty bell-feature config plus shim handling for system sound and/or a macOS attention request | The visual effect is an operating-system best effort and can be affected by focus and system preferences. |
| Compatibility | Ghostty `TERM` and grapheme-width configuration | `Ghostty` uses `xterm-ghostty`; compatibility modes use `xterm-256color`, with the legacy mode selecting legacy grapheme widths. A process enabling terminal mode 2027 forces Unicode widths as documented by Ghostty. |

The authoritative option names and edge cases are in the [pinned Ghostty manual](../../native/artifacts/osx-arm64/ghostty/doc/ghostty.1.md). Unsupported future settings must be represented as capabilities or documented policy-only values rather than silently ignored.

Close is two-phase. The engine reports active work without displaying UI. The session host returns `ConfirmationRequired`; presentation asks once with scope-aware wording; a confirmed active session is force-terminated and audited distinctly from a graceful close. Cancellation and engine failure are separate outcomes.

Desktop v1 keeps a session for the lifetime of its owning panel. A future server terminal backend is selected independently and does not change application contracts.

## Consequences

- Incidental native-control recreation no longer kills the shell.
- One host operation can preflight all sessions owned by a tab or window.
- libghostty can change behind the shim without entering Core, Protocol, or App.
- Explicit process launches preserve argv boundaries and environment values without shell interpolation.
- Per-terminal render profiles do not leak across workspaces or sibling panels.
- Per-session native terminal bindings and Find are supplied by Asura rather than inherited from a global Ghostty keymap.
- Native host-key callbacks remain memory-safe when a session is abandoned or teardown is deferred; finalizers never block on the AppKit main thread.
- The native physical-input gate and monotonic input epoch prevent queued agent
  input from overtaking later human input across every supported AppKit input
  path.
- Existing version-1 native callers and schema-1 terminal-profile payloads retain compatible behavior as settings are added.
- macOS remains the first native renderer; Windows/Linux require a conforming M2 adapter.

## Alternatives rejected

- Letting each control prompt and dispose independently cannot provide aggregate or atomic preflight.
- Exposing libghostty structs through managed code would couple the application to an unstable ABI.
- Treating detach as close would make future server reconnect semantics impossible.
