# ADR 0006: Semantic themes with platform visual profiles

- Status: Accepted
- Date: 2026-07-22

## Context

The Pencil design is a strong dark macOS composition, but Asura must feel natural on macOS, Windows 11, GNOME, and KDE and must follow accessibility settings.

## Decision

Define application colors, typography roles, spacing, radii, elevation, status, focus, and terminal ANSI colors as semantic tokens. A platform-profile adapter maps those tokens to host metrics, chrome, materials, focus, motion, and conventions. Automatic mode is the default.

Appearance input comes from Avalonia platform settings first, then optional platform adapters: AppKit on macOS, Windows platform APIs, and XDG Settings portal/desktop identification on Linux. Resolution order is explicit user accent, live host accent, then the bronze fallback. Application and terminal palettes remain independent.

High contrast, reduced motion, reduced transparency, text scale, and material support are capabilities with visible fallbacks. Explicit application typography uses `ShellFontSize8` through `ShellFontSize28` semantic resources derived live from the effective host text scale; numeric font sizes are reserved for non-text glyph assets such as icons, and a repository convention enforces that boundary. Text-bearing controls use minimum dimensions where a fixed design height would clip scaled content. Liquid Glass, Mica/Acrylic, vibrancy, Adwaita-like, and Breeze-like treatments are navigation/chrome profiles, never Core concepts.

## Consequences

- View code consumes semantic resources instead of hard-coded platform meaning.
- Visible application text and terminal accessibility status update without rebuilding the visual tree when host accessibility preferences change.
- Platform appearance can evolve without changing persisted definitions or application operations.
- Custom colors require contrast validation and reset/import/export behavior.
- Native material adapters remain optional; inaccessible or unsupported effects fall back to opaque semantic surfaces.

## Alternatives rejected

- Pixel-copying the Pencil macOS frame on every OS would fight native conventions and accessibility.
- Treating the orange mockup accent as fixed contradicts system-accent behavior.
- Coupling terminal palette changes to application light/dark mode breaks user expectations.
