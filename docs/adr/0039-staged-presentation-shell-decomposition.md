# ADR 0039: Staged presentation shell decomposition

- Status: Accepted
- Date: 2026-07-25
- Builds on:
  [ADR 0006](0006-semantic-themes-and-platform-profiles.md),
  [ADR 0016](0016-host-owned-runtime-workspace-graph.md),
  [ADR 0017](0017-native-dotnet-agent-runtime.md),
  [ADR 0020](0020-native-webview-wrapper-and-first-browser-capability-slice.md)
- Tracks:
  [UI Foundation and Design Coherence issue 1](https://github.com/terion-name/asura/issues/1)
- Terminal-view update: [ADR 0040](0040-cross-platform-libghostty-vt-terminal.md)
  supersedes the native-terminal-child constraint in this record. The staged
  presentation ownership decision remains accepted.

## Context

Asura has strong project boundaries and extensive behavioral coverage, but
its Avalonia presentation has accumulated most desktop orchestration in three
files:

- `MainWindowViewModel.cs` owns navigation, catalog projections, history,
  editors, runtime graph reconciliation, agent targeting, settings workflows,
  and many mutations;
- `MainWindow.axaml` contains runtime panel templates, Launcher, Workspace,
  Settings, the agent workbench, and in-window overlays;
- `MainWindow.axaml.cs` owns native-host focus, dialogs, keyboard routing,
  menus, drag interactions, layout gestures, and close flow.

Those files grew while the product was proving difficult runtime, security, and
recovery invariants. A replacement shell must preserve the working system and
must not duplicate those invariants in presentation code. A big-bang rewrite
would make behavior drift difficult to distinguish from structural movement.

The existing project boundary remains correct: `Asura.App` may depend on
Core and Application contracts, and desktop composition owns concrete terminal,
browser, vault, host, and platform implementations. The problem is ownership
inside the presentation project, not a missing physical project.

The design exports also cover fewer states than the application now implements.
Structural decomposition therefore comes before visual reconciliation. It
creates stable surface ownership and test seams without treating an incomplete
mockup as permission to remove working behavior.

## Decision

Asura will decompose the presentation incrementally. Every structural
slice preserves behavior, keeps the full repository gate green, and lands
independently before any behavior or design change that depends on it.

### Root window ownership

`MainWindow` remains the desktop top-level composition and lifecycle boundary.
It owns only concerns that require a real top-level:

- construction and hosting of one root `ShellView` plus native title/chrome;
- application activation and close lifecycle;
- top-level dialogs, storage pickers, native menus, clipboard, and
  platform-owned focus bridges;
- coordination required by native browser child views. Terminal panels are
  ordinary Avalonia controls and need no native-child coordination.

[ADR 0042](0042-cef-off-screen-browser-runtime.md) later removes the browser
native-child coordination requirement by moving the panel to CEF OSR.

It does not contain route markup, panel templates, feature dialog branching,
definition persistence, catalog mutation, runtime graph policy, agent policy,
MCP policy, secret access, or feature-specific presentation state.

### Route and overlay views

Launcher, Workspace, and Settings become dedicated route views. Command
Palette, New Item, New Panel, Layout Designer, and definition editors become
dedicated overlay or dialog views.

`ShellView` owns the common frame, route host, and docked overlay host. Settings
uses a route shell plus cohesive page components; a page receives a dedicated
view model only when it has independent state or behavior. The goal is explicit
ownership, not one view model or interface for every XAML fragment.

The first extraction retains the existing bindings and `DataContext` contract.
Routes receive narrower surface view models only after the XAML move is proven
behavior-preserving. Overlay focus trap, dismissal, and focus-return behavior
remain observable contracts.

### Surface presentation models

Presentation state is grouped by product surface:

- Launcher and History;
- Runtime Workspace;
- Agent Workspace and exact-scope selection;
- Settings and definition management;
- root route and overlay state.

These models expose display state and typed commands. They do not reproduce
Application, SessionHost, agent-governance, or persistence rules. A temporary
compatibility facade may keep existing bindings working while one surface at a
time migrates; the facade must shrink with each slice and must not become a
second permanent shell.

The target ownership is:

- `ShellViewModel`: active route and overlay, cross-surface command
  availability, global presentation status, and owned route models;
- `LauncherViewModel`: launcher search, history, and open/create projections;
- `WorkspaceSurfaceViewModel`: visible tab, panel, and agent composition plus
  typed workspace commands;
- `SettingsShellViewModel` and cohesive page models: settings navigation and
  editor presentation state.

Existing focused models and runtime panel models are reused. No base-view-model
hierarchy, service bag, or event bus is introduced.

### Runtime coordination

One focused runtime-workspace owner keeps accepted host graph revisions,
receipts, recovery snapshots, live session links, exact runtime identities,
watch cancellation, and resynchronization. Views propose typed operations and
render accepted state. They never optimistically invent graph ownership or
commit topology before a validated host receipt.

This owner depends on Application ports and projections, not concrete terminal,
browser, file-provider, agent-provider, MCP, or storage implementations.
It is window-scoped: one runtime graph, one `WindowInstanceId`, and one
`ClientId`. It exposes named typed operations, not a generic `Execute` method.
The first implementation remains concrete unless a second implementation or
test seam demonstrates the need for an interface.

### Desktop interaction services

Interactions that require a top-level or platform object remain at the desktop
edge: dialogs, clipboard, native menus, storage pickers, close confirmation,
and native focus transfer. They use domain-specific contracts rather than a
general UI service or service locator.

Pointer gestures and keyboard equivalents converge on the same typed
application command. Code-behind is acceptable for Avalonia event translation,
native-view mechanics, and focus handoff; it is not an application use-case
layer.

`Asura.Desktop` remains the composition root. Quick Terminal remains a
sibling top-level with its independent graphless identity; its eventual
decomposition consumes narrow runtime and navigation collaborators instead of
sharing the full root view model.

### Native-view constraints

Terminal and browser hosts remain rectangular, non-transformed, and owned by
their existing presentation hosts and factories. Extraction must preserve
attach/detach, focus, input ownership, clipping, scaling, disposal, and session
identity.

No workflow assumes an Avalonia flyout can paint above an incompatible native
child. Agent, approval, chooser, find, and error surfaces use docked siblings,
separate top-levels, or a platform-native overlay when required.

### Lifecycle and ownership

Every presentation owner has one explicit lifetime:

- the root owns route-level models and cancels them when the window closes;
- a route owns subscriptions used only while that route model exists;
- a runtime workspace owns its graph watch and linked panel presentation
  lifetimes;
- a panel presentation owns only its native or managed view attachment;
- dialogs and transient overlays own their own cancellation and restore focus
  to the initiating surface.

Subscriptions are disposed by the owner that created them. Cancellation is
propagated rather than replaced with detached work. Extraction must not change
the order of close, cancellation, receipt validation, history drain, recovery
write, or native-view disposal.

Constructors establish state and ownership only. Window-scoped asynchronous
observers start explicitly, quiesce idempotently, and dispose exactly once.
`async void` remains limited to Avalonia event boundaries. Shutdown stops new
commands, settles agent and graph watchers, detaches panel presentation,
flushes presentation history, closes the session host, and finalizes recovery
state in the existing proven order.

### Dependency rules

`Asura.App` continues to depend on Core values and Application ports or
projections only. Vendor engines, provider SDKs, SQLite, OS vault
implementations, and process transports remain private to their existing
projects.

An interface is introduced only for a real platform boundary, multiple
implementations, or a test seam that cannot be expressed more simply.
Extraction must not create one-interface-per-class, pass-through layers,
generic managers, universal event controllers, or flag-driven component
frameworks.

Views depend only on their surface model and leaf presentation controls.
Surface models contain no `Window`, `Control`, native handle, Desktop,
Terminal, Browser, SessionHost, or Infrastructure concrete type. No new
physical project is added during the first extraction; the existing dependency
tests continue to enforce the project boundary.

## Characterization ledger

The following existing suites define behavior that structural changes must
preserve:

| Behavior | Primary evidence |
|---|---|
| Routes, overlays, dirty-editor navigation | `MainWindowKeybindingEditorTests`, `MainWindowRuntimeGraphIntegrationTests`, `SavedScreenRuntimeIdentityTests` |
| Runtime graph registration, mutation, revision, recovery, and disposal | `MainWindowRuntimeGraphIntegrationTests`, `MainWindowTabReorderTests`, `SavedScreenRuntimeIdentityTests` |
| Agent target resolution and scope pinning | `MainWindowRuntimeGraphIntegrationTests`, `AgentChatViewModelTests` |
| History, privacy, retention, recovery, and export | `SavedScreenRuntimeIdentityTests`, `RecentSessionHistoryTests`, `RecentSessionHistoryExportControllerTests` |
| Settings validation and persistence | the focused editor view-model suites plus `MainWindowKeybindingEditorTests`, `MainWindowSavedScreenDeleteUndoTests`, and `McpServerProfileEditorViewModelTests` |
| Keyboard commands and panel focus policy | `MainWindowKeyboardPolicyTests`, `ApplicationKeySequenceResolverTests`, `RuntimePanelLayoutPanelTests`, `RepositoryConventionTests` |
| Host close lifecycle | `DesktopRunFinalizerTests`, `CloseLifecycleTests`, and the MainWindow close flow |

Before the owning code moves, focused characterization must close the remaining
top-level gaps around dialog sequencing, cancellation round trips, route focus
restoration, native-menu dispatch, and native-host focus return. Tests remain
at the narrowest existing seam; a new abstraction is added only when it is the
smallest way to expose one of those observable contracts.

Markup characterization searches the owning application view rather than
assuming that every element remains in `MainWindow.axaml`. It still requires a
unique named element, class, handler, and code-behind owner so extraction
cannot weaken the assertions.

## Migration sequence

1. Record this decision, make XAML characterization view-aware, and close the
   shell-interaction gaps.
2. Extract runtime panel views without changing their view models or factories.
3. Extract Launcher, Workspace, Settings, and overlay views while retaining the
   current binding surface.
4. Move event translation to the owning view or a focused desktop interaction
   boundary.
5. Decompose the root view model behind a shrinking compatibility facade.
6. Extract the runtime-workspace owner while the root view model forwards the
   existing public contract; remove forwarding only after bindings and tests
   migrate.
7. Introduce reusable components only for stable concepts with at least three
   real uses.
8. Reconcile each implemented surface with its canonical design and run
   same-state visual and accessibility gates.

Each step is independently reviewable and keeps behavior changes in later,
separate commits.

## Consequences

- The first commits add tests and boundaries rather than visible features.
- MainWindow remains temporarily large while extracted surfaces continue to
  bind through the compatibility facade.
- Some local XAML duplication is acceptable until a component represents a
  stable product concept used by at least three surfaces.
- Runtime and governance correctness remain more important than reducing line
  counts.
- Visual changes gain a clear owner and can be compared screen by screen
  without editing the entire shell.

## Alternatives rejected

- **Rewrite MainWindow in one replacement shell.** This would combine structure,
  behavior, and design changes and discard accumulated runtime invariants.
- **Leave the presentation monolith in place.** It would keep every design
  correction and feature change coupled to unrelated surfaces.
- **Extract a generic UI manager or universal interaction controller.** It
  would hide different lifecycles behind flags and create another monolith.
- **Move Application or governance rules into child view models.** That would
  duplicate authority and make UI state an execution boundary.
- **Build the component kit before surface ownership is known.** Similar markup
  is not sufficient evidence of a shared product concept.
