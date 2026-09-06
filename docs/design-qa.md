# Design QA review tool

The optional visual review tool renders the real Avalonia views with deterministic,
synthetic fixtures. It is offline, reads no user profile or secret store, opens
no terminal or browser service, and labels its window `GhostSHELL · design QA`.
`./scripts/check-design-qa.sh` compares the implementation with the committed
reference at the identical route, content, interaction, viewport, and appearance.

This comparison is not a CI or release gate. Product design evolves independently
of the committed reference images, so a mismatch is review evidence rather than a
correctness failure. Run it explicitly when reviewing or intentionally updating
the visual references.

## Accepted matrix

| Concern | Accepted capture |
| --- | --- |
| Canonical viewport | `workspace` at 1440 × 900 |
| Minimum viewport | `workspace-minimum` at 1080 × 680 |
| Dark, 100% text | `design-system` |
| Light | `design-system-light` |
| High contrast | `design-system-high-contrast` |
| 200% text | `design-system-scale-200` |
| 250% text | `design-system-scale-250` |
| Keyboard focus | `settings-appearance-focused` |
| Empty, loading, offline, permission, unsupported, stale, partial, conflict, retry, terminal error, cancelled, and destructive states | `design-system` variants |

## Primary-surface ledger

| Surface | Accepted captures |
| --- | --- |
| Workspace shell and terminal chrome | `workspace`, `workspace-minimum` |
| Agent | `workspace-agent` |
| File viewer | `workspace-file-viewer` |
| Browser | `workspace-browser` |
| Statistics and process monitor | `workspace-statistics`, `workspace-process-monitor` |
| Docker | `workspace-docker` |
| Git | `workspace-git` |
| Database | `workspace-database` |
| Redis | `workspace-redis` |
| Settings | `settings-appearance`, `settings-workspaces`, `settings-networking`, `settings-terminal`, `settings-quick-terminal`, `settings-keybindings`, `settings-files`, `settings-agent`, `settings-mcp`, `settings-secrets`, `settings-diagnostics`, `settings-about` |
| Workspace isolation and networking | `settings-workspace-editor-isolated` (isolation, runtime image, and host mounts), `settings-workspace-editor-networking` (the same editor at its end: the workspace's own network policy and offered connections), `settings-networking-editor` (the connection editor dialog) |
| Command, panel, and layout overlays | `overlay-command-palette`, `overlay-new-panel`, `overlay-layout-designer` |
| Shared components and semantic states | `design-system` appearance matrix |

## Explicit blockers and platform adaptations

- Native terminal cells and live browser documents are adapter-owned surfaces.
  The headless gate accepts their Avalonia chrome and explicit unavailable state;
  live renderer behavior remains covered by the platform acceptance runners.
- The harness models the current macOS product profile and host accent. Future
  Windows and Linux adaptations are intentionally outside this milestone.
- Native title-bar controls are absent from headless application captures. The
  separate website export adds deterministic macOS traffic lights only for site
  artwork; those decorations are not an application reference.
- Floating native child surfaces are docked or captured as their real standalone
  Avalonia window because a window bitmap cannot include another platform surface.

## Appearance named-host checks

The deterministic review proves resource mapping and the preview/persistence
contract, but it does not prove a compositor or native settings notification.
Before release, a named interactive macOS host must verify all of the following
against one packaged build:

1. With a fresh profile and no workspace accent override, the current macOS
   accent appears in the main workspace, Quick Terminal, settings/dialogs, and
   newly opened sibling windows.
2. Changing the macOS accent while GhostShell is running updates those surfaces
   without replacing terminal session identities or losing scrollback.
3. An explicit saved application accent overrides macOS; selecting Follow host
   restores live macOS tracking.
4. High Contrast and Reduce Transparency replace native material with the opaque
   fallback in the main window and Quick Terminal, including native chrome and
   the docked agent surface. Re-enabling effects restores material without a
   terminal restart.
5. Appearance Preview reaches every open window and both terminal surfaces;
   Cancel restores the exact saved values and Apply survives restart.

Windows 11 and supported GNOME/KDE compositor behavior remains named-host work
for the later cross-platform milestone; a cross-RID build is not that evidence.

An intentional design deviation must be added here with its route and rationale
before its reference is approved.
