# ADR 0016: Host-owned runtime workspace graph

- Status: Accepted
- Date: 2026-07-23

## Context

The session host already owns panel sessions, attachments, input leases, revisions, and close policy, but the desktop UI previously kept the live workspace, tab, and panel hierarchy as presentation-only mutable state. That split left no authoritative ID-addressable target graph for future web, CLI, ACP, A2A, or governed agent clients. It also allowed a UI selection change to occur without a revision check or ordered host event.

The runtime graph is not a durable workspace or saved-screen definition. Opening a definition creates fresh runtime IDs, and replacing or closing a window's active graph must not retain stale agent targets.

## Decision

`Asura.Core` defines immutable `WorkspaceInstance`, `TabInstance`, and `PanelInstance` projections with stable IDs, typed panel kinds, deterministic ordering, active tab/panel IDs, and optional session linkage. Constructors enforce non-empty graphs, unique tab and workspace-wide panel IDs, valid active members, and defensive copies.

`ISessionHostClient` exposes typed register, unregister, query, tab activation, panel activation, and watch operations. The desktop host owns at most one registered runtime workspace graph per window and client. Registration atomically replaces the window's previous graph, while explicit unregister, successful window close, and client disconnect remove it. Every mutation supports an expected revision and produces a monotonic revision plus ordered retained events; lagging watchers receive an explicit resynchronization snapshot, and removal delivers a terminal `Removed` event before stream completion.

The desktop registers a complete candidate graph before exposing a newly opened workspace. Selection changes use ID-targeted host operations, and structural changes submit a complete replacement proposal with the last accepted revision. The view model commits add, split, remove, or rename state only after validating the host receipt, so presentation state follows host-owned runtime truth. Closing the final tab or panel unregisters the graph before returning to the launcher.

Terminal and hosted-file session creation validate the full window/workspace/tab/panel owner and panel kind whenever a graph exists. The host, never a client proposal, supplies `PanelInstance.SessionId`: registration discards proposed links and reconciles the graph against live hosted sessions while session creation, registration, and close share one ordering boundary. A real link or unlink advances the graph revision and sequence once and emits `PanelSessionLinked` or `PanelSessionUnlinked` with the affected session identity. Closing a superseded session only unlinks that exact identity, structural replacement preserves the authoritative current link or authoritative `null`, and closed or failed sessions cannot be relinked.

The desktop consumes the graph watch from the registration cursor, applies host projections on the UI thread, resumes with the authoritative cursor after resynchronization, and refreshes once before retrying a mutation whose conflict proves that the host has a newer revision. Quick Terminal owns a separate native-window identity so its intentionally graphless session cannot be mistaken for a panel in the main window graph.

## Consequences

- Future clients and the governed agent can address one authoritative runtime graph without relying on visual indices or Avalonia objects.
- Rapid mutations are serialized by the desktop client and stale expected revisions fail without changing visible activation or structure.
- Whole-graph replacement is intentionally the first structural command. Dedicated move, resize, or layout-delta operations can be added when another client needs concurrent structural editing.
- Panel session discovery is host-authoritative across initial registration, late creation, reconnect overlap, close, and watcher resynchronization. Client-supplied session links are never trusted.
- The in-process host currently holds one session/graph ordering gate across engine creation and close I/O. This is safe for the desktop slice but can cause head-of-line blocking; standalone, headless, or highly concurrent host modes must replace it with per-session/per-graph coordination without weakening the ordering invariants.
- Layout geometry and zoom remain presentation/recovery concerns in this slice. The host owns runtime identity, hierarchy, ordering, activation, and lifecycle, not pixel layout.
- No graph operation is exposed to an agent before the M3 capability broker and threat model are accepted.

## Alternatives rejected

- Keeping the graph only in view models would create a hidden desktop-only control plane and make stale targets unavoidable.
- Registering graphs without atomic window replacement or explicit unregister would accumulate closed workspaces as actionable targets.
- Addressing tabs and panels by position would make reordering and concurrent clients unsafe.
- Applying UI mutations first and synchronizing afterward would permit rejected host operations to leave divergent runtime truth.
