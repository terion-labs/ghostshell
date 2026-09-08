# ADR 0040: Cross-platform libghostty-vt terminal and Avalonia presentation

- Status: Accepted
- Date: 2026-08-01
- Supersedes:
  [ADR 0001](0001-terminal-session-and-shim-boundary.md) and the terminal-engine
  and presentation split in
  [ADR 0013](0013-windows-linux-terminal-state-and-pty.md); the
  renderer/shim-specific terminal dispatch clauses in
  [ADR 0019](0019-one-action-agent-capability-broker.md) and
  [ADR 0031](0031-governed-terminal-character-chords.md); and the
  native-terminal-child clauses in
  [ADR 0004](0004-native-browser-adapters.md),
  [ADR 0015](0015-durable-quick-terminal-settings.md), and
  [ADR 0039](0039-staged-presentation-shell-decomposition.md)
- Builds on:
  [ADR 0002](0002-in-process-session-host-and-transport.md) and
  the staged presentation ownership retained from
  [ADR 0039](0039-staged-presentation-shell-decomposition.md)

## Context

The first desktop implementation selected two materially different terminals:
full libghostty rendered through an AppKit `NSView` on macOS, while Windows and
Linux used XTerm.NET state with an Avalonia renderer. That split made behavior,
input, and visual fidelity platform-dependent. More importantly, the native
terminal child could not participate normally in Avalonia composition. Native
child z-order, clipping, overlays, docking drop targets, reparenting, and
floating-window transitions repeatedly conflicted with the workspace UI.

GhostSHELL needs one canonical terminal state for human rendering, governed
automation, recovery metadata, and future headless clients. It also needs the
desktop terminal to behave like an ordinary panel so Avalonia owns composition
and interaction for the whole workspace. The experiment to share a Ghostty GPU
surface through IOSurface would retain a second rendering/composition lifetime
and was not justified for this product stage.

Ghostty now provides `libghostty-vt`, a cross-platform C ABI for terminal state
and protocol encoding. Its ABI is still evolving, but it is a substantially
narrower boundary than embedding Ghostty's application renderer.

## Decision

### One terminal engine on every desktop OS

macOS, Windows, and Linux use the same terminal pipeline:

1. Porta.Pty owns the local pseudo-terminal process and transports raw bytes.
2. `libghostty-vt` owns the canonical terminal state, VT parsing, terminal
   protocol replies, key encoding, mouse encoding, paste modes, selection, and
   render damage.
3. GhostSHELL projects that state into immutable Application-owned render and
   automation DTOs.
4. An ordinary Avalonia control renders those DTOs and translates keyboard,
   pointer, focus, clipboard, and IME interaction into typed session-host
   operations.

PTY output is passed to libghostty-vt as raw bytes; it is not decoded to a
managed string first. User input and terminal-generated protocol responses use
the same bounded ordered PTY writer. Process lifecycle remains independent from
presentation attachment, and the session host retains close, input-lease,
human-preemption, cancellation, and audit ownership.

There is no terminal `NSView`, `NativeControlHost`, IOSurface, or externally
owned Metal/OpenGL surface. Avalonia owns terminal z-order, clipping, scaling,
focus, docking overlays, and floating-window composition on every desktop OS.
Native child views may still exist for unrelated product surfaces such as the
browser; they are not part of this terminal decision.

### Human paste and agent authorization

A human paste gesture sends ordinary text immediately, including multiline
text; it does not open a second confirmation dialog. Bracketed-paste framing
follows the live terminal mode. The engine neutralizes terminal editing and
escape controls, including an embedded bracketed-paste terminator, before
encoding the text. Without bracketed paste, line feeds become carriage returns
as expected by the receiving terminal application, so pasted newlines can
execute commands. This is normal terminal behavior, not a promise of command
inspection or sandboxing.

The retired `TerminalPasteSafetyPolicy` values remain readable only to preserve
existing serialized profiles. They have no runtime effect and are not offered
in Settings. This supersedes the unsafe-paste confirmation clauses in ADR 0001
and ADR 0013; their dated acceptance evidence describes the older UI.

Agent paste and atomic text submission still require their independent,
one-use capability authorization and current input lease. A paste request has
no self-approval flag. Removing the human dialog does not grant a model human
authority or bypass the agent approval broker.

### Separate renderer and automation projections

The renderer consumes `TerminalRenderFrame`, not the bounded text snapshot used
by agents. A render frame contains the complete current viewport plus a
revision and `None`/`Partial`/`Full` damage metadata with ordered dirty rows.
Cells preserve width/spacer roles, colors, selection, hyperlinks, semantic
roles, and terminal styles. Underlines retain single, double, curly, dotted,
dashed, and inherited or explicit underline color.

The terminal-controlled cursor is distinct from the profile fallback. Its
render state includes block, bar, underline, or hollow-block shape, visibility,
blink, password-input state, wide-character-tail position, and explicit cursor
color.

Kitty graphics use a generation-qualified image identity and explicit
placement data. Frames carry decoded image content, source rectangles,
viewport geometry, z-order, and storage generation so the Avalonia renderer
can cache content, draw below background/below text/above text, and retire
images when Ghostty's lifecycle advances. Unicode virtual placements use
Ghostty's placement calculation rather than a managed reimplementation.

`TerminalScreenSnapshot` remains a separately bounded automation projection.
It carries typed, bounded live-session OSC 133 lifecycle events and
viewport-resolved command boundaries without making agent correctness depend
on presentation pixels or command-block decoration.

### Deterministic terminal typeface

GhostSHELL embeds the official JetBrains Mono 2.304 regular, bold, italic, and
bold-italic faces pinned by the same Ghostty source snapshot. Avalonia registers
them under an application-owned font-collection key so an installed font with
the same family name cannot replace the reviewed assets. Terminal measurement
and drawing retain the complete resolved `Typeface`, including that collection
identity; reducing it back to a family-name string would reintroduce platform
substitution.

An explicitly selected installed terminal family remains supported only when
Avalonia resolves it as fixed pitch. A missing or proportional selection falls
back to the embedded JetBrains Mono collection on every platform, rather than
to an OS-dependent font catalogue.

The faces come from the official JetBrains Mono package, not Ghostty's test
font resources. Native bootstrap invokes Zig's package fetch for the exact URL
and package hash declared by the pinned Ghostty `build.zig.zon`, then checks
each reviewed face and `OFL.txt` independently by size and SHA-256. The common
artifact has a sorted manifest and a receipt bound to the reviewed font
catalog, Ghostty source commit, Zig package hash, every face, and the OFL text.
The App build fails when an expected face is absent; release packaging retains
the inspectable closure and validates the font and license evidence. Missing
styles fail closed rather than allowing synthetic bold or italic to hide an
incomplete package.

### Narrow tracked Ghostty overlay

GhostSHELL pins Ghostty commit
`08f039fbb3dea9c6b1cdb5ff4550666598122346` and builds its public
`libghostty-vt` C ABI with Zig 0.16.0. C ABI declarations and safe-handle
ownership remain private to `GhostShell.Terminal`; no Ghostty or Porta.Pty type
crosses into Core, Protocol, SessionHost, or App.

The build applies the reviewed patches under `native/ghostty-vt/patches` to a
disposable checkout. The overlay is deliberately limited to gaps needed by the
product:

- a synchronous, size-checked OSC 133 callback normalized to prompt, input,
  executed, and finished events, including an optional exit status;
- a virtual Kitty-placement iterator that delegates to Ghostty's canonical
  `unicode.placementIterator` and `Placement.renderPlacement` implementation;
- Ghostty's existing Wuffs PNG decoder as the default libghostty-vt decoder;
- a bounded full-scrollback search entry point that delegates to Ghostty's
  canonical `ScreenSearch`, including wrapped-row and selection behavior; and
- an exact extension-ABI marker plus native layout assertions so stale or
  mismatched patched libraries fail before a session starts.

The patch must remain reproducible against the pinned source, retain a public C
ABI, and carry upstream Zig tests. Updating the Ghostty pin requires clean
patch application, upstream lib-vt tests, C header validation, managed interop
tests, and desktop terminal conformance.

The build also stages the reviewed Bash, Fish, and Zsh integration resources
byte-for-byte from the same Ghostty commit. The launch adapter changes only the
child-process launch snapshot and preserves the original durable connection and
recovery identity. Disabled, unsupported, incompatible, or missing integration
is reported without inventing semantic shell events.

## Consequences

- Every desktop OS renders and automates the same canonical libghostty-vt
  state; platform differences stop at PTY/native-library distribution and
  Avalonia's host input services.
- Terminal panels participate normally in Avalonia docking, drag targets,
  overlays, clipping, transforms permitted by the UI, and floating windows.
- The desktop no longer ships or loads the retired GhostSHELL AppKit shim, full
  `libghostty` renderer, or XTerm.NET terminal engine.
- Renderer fidelity has explicit contracts for live cursor state, underline
  variants and colors, Kitty image content/placements/lifecycle, semantic shell
  events, and row-level damage rather than relying on a lossy text snapshot.
- The managed renderer owns glyph shaping, drawing, IME presentation,
  selection UI, hyperlinks, accessibility exposure, and image caches. Those
  responsibilities require their own regression and named-host verification.
- Default terminal metrics and styling use the same embedded JetBrains Mono
  family on every host; installed fonts affect a terminal only when the user
  explicitly selects a verified fixed-pitch family.
- Typeface selection is deterministic across supported hosts and carries real
  400/700 normal/italic faces without a system-font prerequisite.
- Named-host rendering, interactive TUI, physical keyboard, IME, clipboard,
  mouse, resize, sleep/wake, and VoiceOver/Narrator/Orca acceptance remain
  release gates. Deterministic unit and integration tests do not turn an
  unobserved platform into a passing release target.
- The project owner accepted the exact macOS native component and staged
  shell-integration license closure recorded in
  `licenses/macos-release-legal.json` without an independent legal review.
  Other platform closures require their own decision.

## Alternatives rejected

- **Keep the macOS `NSView` renderer.** This preserves the composition and
  behavior split that caused terminal overlays and docking to diverge.
- **Render Ghostty into IOSurface or another shared GPU surface.** This adds
  render-thread, texture, resize, and device-lifecycle synchronization while
  still creating a second composition boundary. It is not part of this route.
- **Depend on RoyalTerminal.** It was useful architectural evidence, but
  GhostSHELL owns the small interop and renderer surface it needs and does not
  take RoyalTerminal as a runtime dependency.
- **Keep XTerm.NET as a fallback.** Two canonical states make platform bugs and
  automation/render divergence harder to detect. Missing libghostty-vt is an
  explicit unsupported-runtime failure, not a silent engine substitution.
- **Copy Ghostty placement, shell-integration, or image algorithms into C#.**
  The reviewed build reuses Ghostty's implementation directly where practical
  and exposes only the missing narrow data through the C ABI.
