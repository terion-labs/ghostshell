# Agent panel tool design

- Status: Implemented safe production slice; remaining items are an explicit roadmap
- Date: 2026-08-15
- Scope: the built-in native agent and the seven user-placeable panel kinds
- Builds on: [ADR 0019](adr/0019-one-action-agent-capability-broker.md),
  [ADR 0040](adr/0040-cross-platform-libghostty-vt-terminal.md), and
  [ADR 0042](adr/0042-cef-off-screen-browser-runtime.md)
- Security basis:
  [Agent-to-tool threat model](security/agent-tool-threat-model.md)

## Executive decision

Asura exposes a capability-negotiated tool contribution for each
hosted panel session. Tools operate the panel's typed engine boundary, not its
Avalonia view model and not the desktop's global pointer or keyboard.

The normal interaction loop is:

1. discover an exact panel from the scope-clipped workspace graph;
2. observe fresh panel state and receive a revision plus opaque references;
3. perform one bounded action against that exact revision/reference;
4. wait for a deterministic state transition when necessary; and
5. observe again to verify the result.

Terminal and browser tools need two levels:

- a normal semantic level optimized for reliable agent use; and
- a low-level input level for TUIs, canvas applications, inaccessible web
  controls, and debugging.

Browser scripting and raw DevTools access form a third, explicitly privileged
level. They are not substitutes for semantic tools and are not enabled in the
production capability profile. In particular, arbitrary JavaScript cannot be
made safe by source/result substring filtering: a same-origin script can derive
cookie or storage access dynamically.

Database, Redis, and Docker now have SessionHost-owned typed session adapters.
Their first production tool slice is deliberately read-only. Direct agent
wiring to presentation view models remains forbidden because it would bypass
target binding, one-action authorization, cancellation, audit, and
outcome-uncertainty rules.

## Research and current implementation

### External interaction model

Browserbase's current Browse CLI exposes navigation, accessibility snapshots,
reference-based element actions, typing, uploads, screenshots, waits, viewport
control, getters, predicates, JavaScript evaluation, raw CDP, tabs, network
capture, and coordinate mouse input. Its recommended workflow is
`open -> snapshot -> act by ref -> snapshot`, and its references are refreshed
when page state changes:

- [Browse CLI documentation](https://docs.browserbase.com/integrations/skills/browse-cli)
- [Browse CLI command reference](https://github.com/browserbase/stagehand/blob/main/packages/cli/README.md)
- [Browserbase agent skills](https://github.com/browserbase/skills)

The vendored cmux reference independently reaches the same conclusion for an
embedded browser: P0 includes snapshot, evaluation, waits, semantic actions,
keyboard, scrolling, getters, predicates, and screenshots; network, emulation,
and raw input are power features. See
[`references/cmux/docs/agent-browser-port-spec.md`](../references/cmux/docs/agent-browser-port-spec.md).

The useful lesson is the workflow and command coverage, not its trust model.
Asura must continue to use exact hosted sessions, trusted risk labels,
one-action authorizations, origin containment, bounded results, and no
transparent mutation retry.

### CEF feasibility

The vendored Exclr8CEF revision already exposes the required primitives:

| Agent need | Existing CEF boundary |
| --- | --- |
| Accessibility snapshot | `CefBrowser.Accessibility.GetFullTreeAsync` |
| DOM identity and geometry | `Dom.DescribeNodeAsync`, `GetBoxModelAsync`, `GetContentQuadsAsync` |
| Scroll/focus element | `Dom.ScrollIntoViewAsync`, `Dom.FocusAsync` |
| Screenshot | `Page.CaptureScreenshotAsync` / `CapturePageAsync` |
| Exact text insertion | `Input.InsertTextAsync` |
| Gesture scroll/tap | `Input.SynthesizeScrollGestureAsync`, `SynthesizeTapGestureAsync` |
| Raw mouse | `SendMouseMove`, `SendMouseClick`, `SendMouseWheel` |
| Raw keyboard | `SendKeyEvent` |
| Page evaluation | `EvaluateJavaScriptAsync` or CDP `Runtime.evaluate` |
| Typed CDP | `ExecuteDevToolsMethodAsync` and domain clients |
| Network events/body | `CefBrowser.Network` |
| Human-visible highlight | `CefBrowser.Overlay` |
| OOPIF/worker discovery | `CefBrowser.Target` |
| File chooser/download | `FileDialog` and download callbacks |

These are present under `vendor/exclr8cef/src/Exclr8Cef`. A private typed CEF
adapter now implements bounded AX snapshots, opaque element leases, live
backend-node revalidation, and acknowledged CDP input. No CEF/CDP object or
JavaScript string enters the normal Application or SessionHost interaction
contracts.

### Current panel inventory

`PanelKind.Placeholder` is a layout affordance with no hosted session and
receives no panel-session tools. It remains part of the trusted workspace
graph: a launcher-only workspace is a valid agent target and still exposes
graph, intrinsic, and governed workspace-layout tools.

| Panel | Hosted session now | Current agent tools | Main gap |
| --- | --- | --- | --- |
| Terminal | Yes | screen/scrollback observation and search, viewport control, bounded waits, text/paste/key/chord/mouse, interrupt, resize | selection and destructive history operations intentionally absent |
| Browser | Yes | state, AX snapshot, bounded waits, semantic click/fill/check, atomic mouse/key/scroll, navigation | artifacts, diagnostics, and safely constrained scripting |
| File Viewer | Yes | list/search/stat/read/access, transfer status, mkdir, bounded text create/replace, same-provider file copy and move/rename, recursive or non-recursive delete | cross-provider copy, ACL mutations, and non-text artifacts |
| Statistics | Yes | one bounded read | no essential gap |
| Process Monitor | Yes | bounded filtered, sorted, and paged list | optional exact-row refresh; no control tool is justified |
| Database Viewer | Yes | bounded relational schema/projected table reads and Redis scan/read/index discovery/search | enforced read-only SQL and all writes intentionally absent |
| Docker | Yes; embedded terminal/file children remain distinct | state, inspect, logs, bounded file reads, exact local macOS lifecycle changes | generic exec and remote/cross-platform lifecycle intentionally absent |

### Implemented production tool inventory

This table is authoritative for the first production slice. Later sections
retain the researched target surface and mark deferred tools explicitly.

| Panel | Production tools |
| --- | --- |
| Common graph/layout | `workspace.inspect`, `tab.list`, `panel.list`, `panel.inspect`, `panel.focus`, `tab.create`, `tab.close`, `panel.add`, `panel.split`, `panel.close` |
| Web | `http.fetch`, `web.read`, `web.search` |
| Terminal | `terminal.read_screen`, `terminal.read_screen_diff`, `terminal.find_on_screen`, `terminal.find_rendered_history`, `terminal.jump_to_rendered_history`, `terminal.read_scrollback`, `terminal.find`, `terminal.scroll_viewport`, `terminal.wait`, `terminal.send_text`, `terminal.paste`, `terminal.submit_text`, `terminal.send_keys`, `terminal.send_chord`, `terminal.send_mouse`, `terminal.interrupt`, `terminal.resize` |
| Browser | `browser.read_state`, `browser.snapshot`, `browser.wait`, `browser.click`, `browser.fill`, `browser.check`, `browser.mouse`, `browser.key`, `browser.scroll`, `browser.navigate`, `browser.back`, `browser.forward`, `browser.reload`, `browser.stop` |
| File Viewer | `files.list`, `files.search`, `files.stat`, `files.read`, `files.access_read`, `files.transfers`, `files.mkdir`, `files.create_text`, `files.replace_text`, `files.copy`, `files.move`, `files.delete` |
| Statistics | `statistics.read` |
| Process Monitor | `processes.list` |
| Relational database | `database.read_state`, `database.list_objects`, `database.describe_object`, `database.read_table`, `database.schema_graph` |
| Redis | `redis.scan`, `redis.read`, `redis.list_indexes`, and capability-gated `redis.search` |
| Docker | `docker.read_state`, `docker.inspect`, `docker.logs`, `docker.files_list`, `docker.files_stat`, `docker.file_read`, `docker.container_start`, `docker.container_stop`, `docker.container_restart`, `docker.container_pause`, `docker.container_resume`, `docker.container_remove` |

`browser.evaluate` exists only as a dormant conformance candidate and is not
advertised by the production browser profile. `browser.cdp`, diagnostics,
artifacts, uploads/downloads, database/Redis writes, remote/cross-platform
Docker lifecycle actions, and generic terminal/Docker exec are not implemented
as production tools.

The three web tools are run-scoped observations governed by the existing
`WebFetch` policy and use one exact typed action/authorization path. They do
not require a visible browser panel.

- `http.fetch` accepts only credential-free HTTP(S) URLs and `GET` or `HEAD`.
  It uses `SocketsHttpHandler` rather than a subprocess, disables automatic
  redirects and cookies, revalidates up to five redirect legs, rejects mixed
  public/private DNS answers, and connects to an admitted address through
  `ConnectCallback`. Only bounded textual, JSON, XML, form, and JavaScript
  media types are returned.
- `web.read` creates a detached isolated CEF context, validates every network
  resource destination, and extracts the current rendered DOM using only
  assembly-owned JavaScript. Its default `markdown` mode applies a managed
  Mozilla Readability port and sanitized HTML-to-Markdown conversion. Explicit
  `rendered_html` mode returns the bounded current DOM, not original response
  bytes.
- `web.search` performs an anonymous Google search in the same detached CEF
  isolation and permits only Google top-level navigations. Fixed JavaScript in
  an isolated world finds semantic `h3` result headings inside `#rso`, reads
  each heading's nearest anchor and language-bearing result block, removes
  hidden label wrappers from a clone, and returns bounded `{url, desc}` entries.
  The host validates HTTP(S) URLs and removes duplicates. Consent,
  unusual-traffic, and challenge pages fail explicitly.

All returned content is labeled `untrusted_web`; model-supplied scripts,
headers, request bodies, credentials, cookies, provider URLs, and selectors
are outside this surface. Browser and network contexts are destroyed after
each call. No Google-specific policy toggle is added.

## Cross-panel contract

### Targeting

- Exact panel/session targets omit `panel_id`.
- Workspace or tab scopes require one `panel_id` enumerated from the fresh
  capability-specific eligible set, even when only one panel is eligible.
- Every prepared action narrows to one exact panel session before approval.
- Cross-panel actions, such as a file transfer or browser upload, bind both
  exact sessions and both current revisions in one authorization.
- Presentation labels are untrusted descriptions and never provide authority.

### Capability negotiation

A tool is advertised only when all of the following are true:

1. the panel is the current active graph-owned session;
2. the session advertises the exact operation capability;
3. a live renderer/attachment exists when the operation needs one;
4. the runtime has the required host-side broker/composer contribution; and
5. the operation can uphold human-preemption and outcome semantics.

Tool definitions are rebuilt after every provider tool-result round, matching
the existing contribution architecture in
`GovernedAgentRuntime.ToolContributions.cs`.

### Revisions, references, and receipts

Observation results use three distinct clocks where applicable:

- `session_revision`: hosted lifecycle/capability state;
- `content_revision`: terminal grid, file listing, monitor sample, or database
  result revision; and
- `document_revision`: committed top-level browser document.

Opaque references are host-generated capabilities to address an observed
object, not selectors or authority on their own. Each reference is bound to
the panel/session, relevant revision, snapshot nonce, object identity, and an
expiry. The host revalidates the underlying object immediately before action.

Mutations return a typed commit receipt. Once an operation crosses its commit
point, cancellation cannot turn it into a safe retry. A missing or invalid
post-commit result is `*_outcome_unknown`, stops provider continuation, revokes
run authority, and requires human inspection, following the existing browser
and file mutation rules.

### Result bounds

- Ordinary JSON tool results remain at or below 64 KiB serialized.
- Text fields use strict Unicode and rune-safe byte limits.
- Rows, nodes, cells, log lines, network records, and matches have independent
  count and byte limits plus explicit `truncated` state.
- Future images, downloads, uploads, exports, and large response bodies require
  an opaque, run-scoped artifact broker. Until that broker exists, those tools
  remain absent; tools never accept or return an ambient local filesystem path.
- All terminal, browser, file, database, Docker, and process content is marked
  untrusted in provider results.

### Waits instead of polling

Long-lived interactive panels should expose bounded waits. A wait has one
condition (including an explicit delay/read-after condition), a caller-selected
deadline of at most one hour, cancellation, and a final fresh snapshot. It does
not synthesize input and never retries another action. Short waits remain the
normal default; the one-hour ceiling exists for builds, remote jobs, downloads,
and other legitimately long interactive work.

### Input ownership

Terminal and Browser have human-preemptible agent input barriers. Browser uses
`browser.agent_input_barrier` and a one-action input lease.
Physical user input advances the input epoch and preempts an agent lease. Raw
input is panel-relative only; neither terminal nor browser tools may move the
desktop cursor, type into another application, or address screen coordinates.

## Common panel tools

Keep the existing graph tools for every hosted operational panel:

| Tool | Purpose | Capability / risk |
| --- | --- | --- |
| `workspace.inspect` | Read the run's one trusted workspace and its fresh graph metadata | `Search` / Observation |
| `tab.list` | List scope-visible tabs and their graph identities | `Search` / Observation |
| `panel.list` | List scope-visible panels, kinds, and hosted-state summaries | `Search` / Observation |
| `panel.inspect` | Fresh identity, lifecycle, health, focus, visibility, activity, and supported operations | `Search` / Observation |
| `panel.focus` | Activate the exact containing tab and panel | `RunCommands` / Routine |
| `tab.create` | Create an active tab containing one selected panel kind | `WorkspaceLayout` / Mutation |
| `tab.close` | Close one exact tab and its sessions | `WorkspaceLayout` / Destructive |
| `panel.add` | Add one selected panel kind to an exact tab | `WorkspaceLayout` / Mutation |
| `panel.split` | Split one exact panel left/right or top/bottom and create a selected panel kind in the new cell | `WorkspaceLayout` / Mutation |
| `panel.close` | Close one exact panel and its session | `WorkspaceLayout` / Destructive |

Every agent run is already bound to exactly one trusted workspace.
`workspace.inspect` therefore accepts only `{}` and resolves that workspace
from the host-owned run context. There is no `workspace.list` tool and no
model-supplied workspace identifier.

Layout mutations are advertised only for a complete `Workspace` run. Their
schemas enumerate current graph-owned tab/panel IDs and panel kinds supported
by the attached desktop. Preparation binds the complete ordered topology;
SessionHost consumes one `WorkspaceLayout` authorization and the trusted UI
port rejects graph drift without retry. Close operations reject unsaved
database edits. Once UI dispatch begins, a missing or unverifiable final graph
is the non-retryable tool failure `workspace_layout_outcome_unknown`; the agent
may inspect the fresh local graph but must not repeat the mutation automatically.
A newer graph is valid when it preserves the exact applied effect. Moving and
resizing panels remain deferred.

## Terminal panel

### Design decision

Do not add a subprocess-style `terminal.exec` abstraction. The product promise
is control of the exact interactive PTY the user sees, including shells, REPLs,
full-screen TUIs, prompts, remote hosts, and container terminals. Command exit
codes are available only when shell integration actually observed them.

Prefer text/state operations for normal shells and semantic shell events for
completion. Use key and mouse input only for interactive applications. The
agent reads terminal modes before deciding whether a wheel belongs to hosted
scrollback or to the remote TUI.

### Tool set

| Tool | Arguments and result | Capability / risk | Status |
| --- | --- | --- | --- |
| `terminal.read_screen` | No required args. Returns bounded visible rows, cursor, dimensions, title, cwd, alternate-screen, paste/mouse modes, scrollback counts, content revision, and recent OSC 133 boundaries/events. | `TerminalRead` / Observation | Implemented |
| `terminal.read_screen_diff` | Exact previously observed content revision plus a changed-row limit. Returns only changed rendered viewport rows. If that revision is not the latest observed screen, reports `baseline_available: false` and no invented diff. | `TerminalRead` / Observation | Implemented |
| `terminal.find_on_screen` | Exact literal text and maximum matches against the current rendered viewport, including alternate-screen TUIs. This is deliberately distinct from scrollback/history search. | `TerminalRead` / Observation | Implemented |
| `terminal.find_rendered_history` | Exact literal text, direction, and maximum matches across the engine-retained rendered row space: offscreen TUI rows plus the current screen. Returns revision-bound opaque row anchors without moving the viewport. | `TerminalRead` / Observation | Implemented |
| `terminal.jump_to_rendered_history` | One opaque row anchor from `terminal.find_rendered_history`. Moves the hosted viewport directly to that rendered row and returns a fresh screen without sending input to the application. | `RunCommands` / Routine | Implemented |
| `terminal.read_scrollback` | `anchor: top|bottom|before|after`, optional opaque row anchor, `max_lines` (16/64/200). Non-mutating bounded history read. | `TerminalRead` / Observation | Implemented |
| `terminal.find` | Exact literal text, direction, maximum matches. Returns bounded excerpts and opaque match anchors without moving the user's viewport. | `TerminalRead` / Observation | Implemented |
| `terminal.scroll_viewport` | `direction: up|down|top|bottom`, `unit: line|page`, bounded `amount`. Reject on alternate screen when there is no hosted scrollback. Returns the resulting viewport snapshot. | `RunCommands` / Routine | Implemented |
| `terminal.send_text` | Exact printable text, no Enter, max 2,048 UTF-8 bytes. | `RunCommands` / Mutation | Implemented |
| `terminal.paste` | Exact bounded text using bracketed-paste semantics; controls escaped in approval. | `RunCommands` / Mutation, human/confirmed policy only | Implemented |
| `terminal.submit_text` | Paste exact bounded text and press protocol-correct Enter in one atomic PTY delivery; preferred for submitting shell commands and interactive prompts. | `RunCommands` / Mutation, human/confirmed policy only | Implemented |
| `terminal.send_keys` | One named special key plus known modifiers and an optional bounded repeat count (1–64), delivered as one queued PTY write. Extend the enum only as libghostty-vt proves encoding. | `RunCommands` / Mutation | Implemented |
| `terminal.send_chord` | One lowercase ASCII letter with exactly Control or Alt. | `DestructiveTerminalActions` / Destructive | Implemented |
| `terminal.send_mouse` | One zero-based cell move/down/up/drag/wheel event, modifiers, and expected content revision. Valid only inside current dimensions and when terminal mouse tracking supports the event. | `RunCommands` / Mutation | Implemented; revision-atomic at PTY dispatch |
| `terminal.wait` | One of delay/read-after, text, newer revision, stable screen, prompt-ready, or command-finished; caller-selected timeout up to one hour. Returns a fresh screen. | `TerminalRead` / Routine | Implemented |
| `terminal.interrupt` | One typed interrupt. | `DestructiveTerminalActions` / Destructive | Implemented |
| `terminal.resize` | Exact bounded cell dimensions, preserving attachment-owned scale. | `RunCommands` / Mutation | Implemented |

Selection read/write and clear-scrollback are not needed for reliable agent
operation. They should remain human UI features until a concrete agent workflow
justifies their privacy and destructive semantics.

### Terminal-specific rules

- `read_screen` never auto-scrolls.
- `find_on_screen` searches only the rendered viewport; `terminal.find` searches
  hosted shell scrollback; `find_rendered_history` searches the separate
  engine-retained rendered row space used by full-screen TUIs. Agents jump to
  a rendered match only with its opaque revision-bound anchor.
- `read_screen_diff` accepts only the engine's most recently observed screen as
  a baseline. Renderer, health, and context reads are not agent observations and
  do not replace it. A later agent-visible screen/find/wait/diff observation
  supersedes it. A stale or unavailable revision yields an explicit unavailable
  baseline rather than a best-effort reconstruction.
- Interactive applications may emit the generic terminal state protocol with
  an optional half-open viewport input range named by exact zero-based
  `row`, `start_column`, and `end_column_exclusive` fields. The host exposes it
  as expiring, untrusted observation only and omits it when it does not fit the
  observed viewport. No screen-text heuristic invents input or approval
  semantics.
- `scroll_viewport` manipulates local hosted history; wheel events sent to a TUI
  remain `send_mouse` mutations. The host never guesses between those effects.
- Mouse coordinates are checked against the same screen revision and grid used
  by the agent. Resize or content-mode drift fails stale.
- `wait(prompt-ready)` is offered only when shell integration is active. No
  prompt regex heuristic becomes authority.
- For a full-screen TUI or REPL without OSC 133, automation uses a content
  revision barrier followed by a stable-screen wait and then a fresh screen
  read. Stability proves only that the rendered grid stopped changing for the
  requested interval; it does not prove that the application is idle, ready
  for input, showing a modal, or requesting approval.
- Screen `text` is logical text: physical rows joined by terminal soft wrapping
  are returned as one logical line. Arbitrary cursor-addressed TUI decoration
  remains visible because removing it heuristically would also remove real
  content.
- Interactive applications may opt into `terminal.interactive-state.v1` by
  emitting an OSC 777 desktop-notification payload with a strictly increasing
  `sequence`, one of `idle_input`, `working`, `streaming`, `modal`,
  `input_required`, or `approval_required`, and a bounded `ttl_ms`. The state is
  exposed as expiring `untrusted_terminal_protocol` observation. Absence means
  unknown, stale/replayed payloads are ignored, and the signal never grants
  approval or agent authority. A `clear` state removes the observation.
  The wire form is `OSC 777 ; notify ; terminal.interactive-state.v1 ; JSON ST`,
  for example
  `{"sequence":7,"state":"streaming","ttl_ms":5000}`. A cooperating app
  refreshes the TTL while the state remains active. Local PTY processes receive
  `ASURA_INTERACTIVE_STATE_PROTOCOL=terminal.interactive-state.v1` so an
  app-neutral launcher can discover support without identifying Asura or
  any particular interactive application.
- App-specific adapters may translate a structured local event stream into
  that protocol. Without an explicit protocol, screen-text recognition can be
  advisory at most and MUST NOT produce a semantic approval action.
- Every input tool requires `terminal.agent_input_barrier`; human input wins.
- No text/chord/paste input is automatically retried after PTY dispatch.

## Browser panel

### Architecture

The private CEF automation adapter sits behind `IEmbeddedBrowserView`. Public
Application contracts remain typed and engine-neutral. The adapter uses CEF's
CDP domain clients and OSR input APIs; it does not expose `CefBrowser`, backend
node IDs, JavaScript object IDs, or raw JSON outside `Asura.Browser`.

The semantic snapshot is built primarily from Chromium's Accessibility tree.
Interactive nodes receive random opaque refs. Internally a lease may contain
the main-frame/document identity, frame ID, backend DOM node ID, accessibility
role/state, snapshot nonce, and geometry evidence. Node IDs are never returned
to the provider.

A reference expires on top-level navigation, renderer replacement, next
snapshot, completed mutation, or a short time limit. Before using it, the host
re-resolves the backend node and rechecks frame, role, visibility, enabled/edit
state, and origin. A replaced node is stale even if a selector would find a
similar replacement.

Element clicks use real CEF/CDP input at a revalidated visible point, not a page
realm `.click()` call. Typing uses focus plus `Input.insertText`/key dispatch so
framework event handlers observe normal input. Fixed semantic actions use an
isolated world only where DOM property access is unavoidable.

### Normal observation and navigation tools

The production subset here is state, snapshot, wait, and navigation. Screenshot,
getters, and predicates remain roadmap items pending artifact/inspection ports.

| Tool | Arguments and result | Capability / risk |
| --- | --- | --- |
| `browser.read_state` | URL, origin, title, load state, history flags, focused state, viewport CSS size/scale, document revision, active downloads, and input epoch. | `BrowserData` / Observation |
| `browser.snapshot` | `interactive_only`, optional text `filter` and `max_depth`; returns a lean bounded accessibility tree and opaque refs. Filtering keeps ancestors and occurs before the node cap. Provider projection has no separate fixed node cutoff. | `BrowserData` / Observation |
| `browser.screenshot` | `viewport|full_page`, optional bounded clip, PNG/JPEG/WebP quality; returns an image attachment/artifact and the exact document/viewport revision. | `BrowserData` / Observation |
| `browser.get` | Ref plus `text|value|html|attribute|box|styles|accessible_name`. Attribute/style names use bounded allowlists. | `BrowserData` / Observation |
| `browser.is` | Ref plus `visible|enabled|checked|selected|editable|focused`. | `BrowserData` / Observation |
| `browser.wait` | One of delay/read-after, load state, URL pattern, text, ref state, document revision, or network idle; caller-selected timeout up to one hour. | `BrowserData` / Routine |
| `browser.navigate` | Absolute HTTP(S) URL or `about:blank`, with current origin/start revision bound into approval. | `BrowserNavigation` / Mutation |
| `browser.back` / `browser.forward` / `browser.reload` / `browser.stop` | No ambient target args. Return final state or a typed no-op. | `BrowserNavigation` / Mutation |

### Normal semantic interaction tools

Production currently advertises click, fill, and ensure-checked. Type, select,
hover, focus, scroll-into-view, highlight, and desired false check state remain
roadmap items.

| Tool | Arguments and result | Capability / risk |
| --- | --- | --- |
| `browser.click` | Ref, `button`, click count, modifiers. Real input dispatch at a revalidated point. | `BrowserInteraction` / Mutation |
| `browser.fill` | Ref and replacement text; empty text clears. Restricted to fillable controls and verifies final value. | `BrowserInteraction` / Mutation |
| `browser.type` | Optional ref, text, optional bounded per-character delay. Focuses and appends through input semantics. | `BrowserInteraction` / Mutation |
| `browser.check` | Ref; idempotently ensures the control is checked and verifies the final state. Desired false state is a future contract. | `BrowserInteraction` / Mutation |
| `browser.select` | Select ref plus one or more exact option values/labels from a fresh snapshot. Returns selected values. | `BrowserInteraction` / Mutation |
| `browser.hover` | Ref; moves the panel-local pointer and returns resulting cursor/hover state. | `BrowserInteraction` / Mutation |
| `browser.focus` | Ref; focuses a page control without moving desktop focus elsewhere. | `BrowserInteraction` / Routine |
| `browser.scroll_into_view` | Ref and alignment. Returns fresh geometry. | `BrowserInteraction` / Routine |
| `browser.highlight` | Ref, bounded duration. Uses CDP Overlay for human-visible action preview. | `BrowserInteraction` / Routine |

### Low-level input tools

These are necessary for canvas applications, custom editors, drag surfaces,
games, remote consoles, and pages with incomplete accessibility trees. They
remain panel-local and require the browser input barrier.

Production intentionally exposes only atomic `mouse` move/click/wheel, atomic
`key` press, and `scroll`; split down/up and drag gestures remain deferred until
their multi-event commit and capture-loss receipts are specified.

| Tool | Arguments and result | Capability / risk |
| --- | --- | --- |
| `browser.mouse` | `move|down|up|click|wheel`, viewport-relative CSS `x/y`, button, click count, wheel deltas, modifiers. Coordinates bind to viewport and document revision. | `BrowserInteraction` / Mutation |
| `browser.key` | `press|down|up`, normalized key/code, known modifiers, repeat flag. Text insertion remains `type`/`fill`. | `BrowserInteraction` / Mutation |
| `browser.drag` | Source ref or point, destination ref or point, button/modifiers, bounded steps. One host-owned gesture with capture-loss handling. | `BrowserInteraction` / Mutation |
| `browser.scroll` | CSS-pixel deltas and optional origin point; uses wheel or synthesized gesture and returns resulting viewport state. | `BrowserInteraction` / Mutation |

### Script and DevTools power tier

This entire tier is deferred in the production profile. The candidate
`browser.evaluate` implementation is retained for conformance work only; an
isolated JavaScript world is not a credential boundary because it still has
same-origin DOM, cookie, and storage access.

| Tool | Contract | Capability / risk |
| --- | --- | --- |
| `browser.evaluate` | Bounded JavaScript source, `world: isolated|main`, optional await, 5-second default/30-second max. Returns JSON-serializable data only, max 64 KiB; no object handles. It binds exact origin/document revision. Main-world use is always explicitly approved. | new `BrowserScripting` / Privileged |
| `browser.cdp` | One method plus bounded params and timeout. The production model-facing allowlist is versioned per CEF build. Prefer typed tools for Input, navigation, files, permissions, downloads, and target lifecycle. No arbitrary raw CDP stream is enabled by default. | new `BrowserDiagnostics` / Privileged |
| `browser.console_read` | Read bounded console and exception buffers with timestamps and source URLs. | `BrowserDiagnostics` / Observation |
| `browser.console_clear` | Clear this panel's host-side diagnostic buffer; it does not run page code. | `BrowserDiagnostics` / Mutation |
| `browser.network_records` | Read bounded, redacted request metadata from an existing capture. Authorization, cookies, Set-Cookie, proxy credentials, and secret-shaped values are removed. | `BrowserDiagnostics` / Observation |
| `browser.network_capture` | Start, stop, or clear the exact panel's bounded diagnostic capture. | `BrowserDiagnostics` / Privileged |
| `browser.network_body` | Read one exact request ref's redacted, size-limited, same-origin response body. | `BrowserDiagnostics` / Privileged |

`browser.cdp` is deliberately not “any method because CEF accepts it.” The
allowlist excludes `Browser.close`, arbitrary `Target.*` attachment/control,
security bypass, permission grants, download-path changes, local-file access,
cookie/auth extraction, and unbounded tracing. A future explicit developer mode
may expose a wider list, but only to exact-panel runs with a human approval per
call or explicitly confirmed run-local Full access; never through `Auto`.

Both script worlds and every allowed CDP method run under the same frozen-origin
navigation guard as normal browser interaction. A script-initiated top-level
navigation cannot bypass the approved origin simply because the script itself
was approved.

### Uploads and downloads

| Tool | Contract | Capability / risk |
| --- | --- | --- |
| `browser.upload` | Element ref plus one or more run-scoped artifact refs; never an arbitrary path. The broker validates size, regular-file identity, MIME/name metadata, lifetime, and user authorization, then resolves the CEF file dialog once. | `ArtifactTransfer` / Privileged |
| `browser.downloads` | Read bounded progress and metadata for this panel's downloads. | `BrowserData` / Observation |
| `browser.download_cancel` | Cancel one exact pending download. | `ArtifactTransfer` / Mutation |

Downloads are denied unless an action has an approved download destination
policy. Accepted bytes enter an owner-only run artifact. The result exposes a
sanitized name, media type, size, hash, and opaque artifact ref. Exporting to a
File Viewer is a separately authorized cross-panel file copy or move.

### Browser-specific rules

- Snapshot and page content are untrusted; instructions found in a page are
  never tool authority.
- Standard navigation and interaction preserve the accepted frozen-origin
  policy. Cross-origin transitions require a new approved navigation action.
- Script/CDP tools cannot smuggle credentials through source, params, URLs, or
  results. Secret use requires the existing secret broker and a purpose-built
  flow; secrets never enter model-visible arguments.
- Input dispatch is the commit point. Post-dispatch ambiguity is never retried.
- Popups map to a future graph-owned Browser panel creation request; they do
  not silently create an untracked CEF target.
- Asura Browser panels are already the product's browser tabs. CEF target
  management must not invent a second hidden tab model. Use workspace/panel
  tools for tab topology.

## File Viewer panel

Keep structured path segments relative to the session's trusted root. Do not
replace them with caller-supplied native paths or provider URLs.

| Tool | Purpose | Capability / risk | Priority |
| --- | --- | --- | --- |
| `files.list` | Bounded directory page with structured child paths and continuation | `ReadFiles` / Observation | Existing |
| `files.stat` | Exact bounded metadata | `ReadFiles` / Observation | Existing |
| `files.read` | Bounded UTF-8 preview | `ReadFiles` / Observation | Existing |
| `files.search` | Provider-capability-gated bounded name search under one directory or subtree | `ReadFiles` / Observation | Implemented |
| `files.mkdir` | One non-root directory, `MustNotExist` | `EditFiles` / Mutation | Existing |
| `files.move` | Move or rename one exact non-root path to one exact non-root destination in the same hosted provider, `MustNotExist` | `EditFiles` / Mutation | Implemented for explicitly trusted local providers |
| `files.delete` | One exact non-root path, with recursive deletion requested explicitly | `EditFiles` / Destructive | Existing, capability gated |
| `files.create_text` | Create one strict UTF-8 file up to 8 KiB, `MustNotExist` | `EditFiles` / Mutation | Implemented for attested WebDAV profiles |
| `files.replace_text` | Replace one strict UTF-8 regular file up to 8 KiB using an opaque stat reference | `EditFiles` / Destructive | Implemented for attested WebDAV profiles |
| `files.copy` | Copy one version-bound regular file up to 64 MiB to a distinct path, `MustNotExist` | `EditFiles` / Mutation | Implemented within one attested provider profile |
| `files.transfers` | List bounded session-owned transfer status without source/destination paths or provider identifiers | `ReadFiles` / Observation | Implemented |
| `files.transfer_cancel` / `retry` | One exact transfer; retry only a provider-declared safely retryable, uncommitted transfer | `EditFiles` / Mutation | Deferred: queue cannot yet prove a race-safe final state |
| `files.access_read` | Bounded POSIX mode or provider ACL | `ReadFiles` / Observation | Implemented |
| `files.access_set` | Exact version-bound mode/grant replacement | `EditFiles` / Privileged | P2 |

Non-text previews return metadata plus an artifact/image attachment when the
provider and model support it. `files.read` must not inline arbitrary binary,
PDF, database, or image bytes into tool JSON.

Cross-panel transfer binds source and destination provider generations,
locations, versions, capabilities, and sessions. A move is not reported as a
single atomic effect when the provider implements copy then delete; partial
transfer remains explicit and non-retryable without inspection.

Ordinary provider `Rename`, `Copy`, and `Move` flags are human-UI capabilities,
not proof of race-safe governed semantics. Each agent mutation needs a separate
production-assigned governed capability, following the existing mkdir/delete
model.

## Statistics panel

The existing `statistics.read` is the correct complete tool set for this panel.
It returns one fresh bounded numeric host snapshot: uptime, logical processors,
observed process counts, aggregate observed CPU/working set, and network rates.

Do not add a generic sampler or arbitrary interval loop. The agent can call the
bounded read again after a user-visible progress update when comparison is
actually needed. Future disk/GPU/temperature metrics should extend the typed
snapshot and capability rather than create raw system APIs.

The current `ProcessControl` capability name is too broad for read-only system
statistics. New policy storage should use a separate `SystemData` capability;
old policy payloads fail closed until migrated explicitly.

## Process Monitor panel

Keep `processes.list` as the primary and, for now, only tool:

- sort is one of CPU descending, memory descending, name ascending, or PID;
- bounded offset/limit pagination can be narrowed by case-insensitive process
  name or exact PID;
- results exclude command line, executable path, user, environment, open files,
  and terminal content; and
- the tool targets only the local hosted Process Monitor, never a remote shell.

Do not add terminate, signal, priority, attach, open-file, or environment tools.
On macOS, sampled PID plus start time does not close the PID-reuse race before a
signal. A future process-control design requires handle-addressed identity,
privilege and confirmation rules, and explicit outcome uncertainty. Porting and
remote process control are separate milestones.

Use a new `ProcessData` capability for listing. Reserve the existing
`ProcessControl` capability for future mutations rather than using a mutation
name for an observation.

## Database Viewer panel

### Hosted architecture

`IDatabasePanelSession : IPanelSession` and its SessionHost factory now bind
the exact panel and provider session.
The session immutably binds driver, connection definition/revision, selected
database, tunnel generation, and a secret reference. The session owns
connection cancellation and publishes only sanitized endpoint/session facts.
The current human presentation keeps its eager direct client while also
creating this governed projection after graph acceptance; agent tools never
receive the direct client or connection string.

Redis uses a hosted Redis session under the same `PanelKind`, but advertises
Redis-specific capabilities and tools. Do not flatten Redis operations into SQL.

### Relational tools

Production implements the first five bounded structural/data observations.
`query_read` and `execute` remain deferred.

| Tool | Purpose | Capability / risk |
| --- | --- | --- |
| `database.read_state` | Driver, server/TLS facts, selected catalog/schema, readiness, capabilities; no connection string/password | new `DatabaseRead` / Observation |
| `database.list_objects` | Bounded tables/views/routines with opaque object refs | `DatabaseRead` / Observation |
| `database.describe_object` | Columns, keys, nullability, types, indexes/relations where available | `DatabaseRead` / Observation |
| `database.read_table` | Structured filters/sorts/page and include/exclude column projection against one object ref; bounded rows/cells/bytes plus distinct filtered/table counts | `DatabaseRead` / Observation |
| `database.schema_graph` | Bounded table/foreign-key graph, optionally clipped to named objects | `DatabaseRead` / Observation |
| `database.query_read` | One bounded SQL statement under an enforced read-only connection/transaction and read-only principal | `DatabaseRead` / Observation |
| `database.execute` | One exact SQL statement or structured table change; SQL hash and sanitized preview bound into approval | new `DatabaseWrite` / Privileged |

SQL text parsing is not a security boundary. `query_read` is available only
when the driver can enforce read-only behavior at the server/transaction level;
otherwise the tool is absent. Stored procedures, multi-statements, DDL, session
settings, and provider-specific escape commands are never accepted by the read
tool merely because a parser labels them SELECT-like.

`database.execute` is never automatically retried. Rows affected and committed
transaction identity form the receipt. Disconnect, timeout, or malformed output
after dispatch is `database_outcome_unknown`.

### Redis tools

Production implements scan, exact read, and Search-index discovery; search is
advertised only when the live provider reports its search capability. All
mutations remain deferred.

| Tool | Purpose | Capability / risk |
| --- | --- | --- |
| `redis.scan` | Pattern plus opaque cursor and bounded count | `DatabaseRead` / Observation |
| `redis.read` | Exact opaque key ref, type, TTL, size, and bounded entries | `DatabaseRead` / Observation |
| `redis.list_indexes` | Bounded Search-index names for the selected Redis database | `DatabaseRead` / Observation |
| `redis.search` | Exact index and bounded query/results when supported | `DatabaseRead` / Observation |
| `redis.set` | Type-specific exact set/append/update with TTL/precondition where supported | `DatabaseWrite` / Mutation |
| `redis.expire` | Exact key and desired TTL/persist state | `DatabaseWrite` / Mutation |
| `redis.delete` | Exact key or collection entry | `DatabaseWrite` / Destructive |
| `redis.publish` | Exact channel and bounded payload | `DatabaseWrite` / Mutation |

Subscriptions are a human live-view feature, not a good finite agent tool.
When an agent needs messages, add a bounded `redis.read_messages` observation
over a separately established subscription rather than leaving a provider tool
call open indefinitely.

## Docker panel

### Hosted architecture

`IDockerPanelSession : IPanelSession` now immutably binds the exact
local/SSH connection definition and Docker engine generation, owns refresh/log
operations, and exposes typed operations currently reached through
`IDockerEngineClient`. The main Docker panel, its embedded container terminal,
and its embedded file session keep distinct session identities and roles in the
workspace graph.

### Tool set

Production implements the six observation tools plus six exact local macOS
lifecycle tools. Shell creation remains deferred; there is no generic Docker
exec tool.

| Tool | Purpose | Capability / risk |
| --- | --- | --- |
| `docker.read_state` | Engine facts and bounded container/image/volume/network summaries | new `DockerData` / Observation |
| `docker.inspect` | Exact resource ref with bounded normalized allowlisted properties; no raw JSON, command, environment, labels, mounts, or host paths | `DockerData` / Observation |
| `docker.logs` | Exact container ref, bounded lines, before/since cursor, text filter/context | `DockerData` / Observation |
| `docker.files_list` / `docker.files_stat` / `docker.file_read` | Bounded container/image/volume file observation | `DockerData` / Observation |
| `docker.container_start` | Start one exact created/exited container | existing `Docker` / Destructive |
| `docker.container_stop` | Stop one exact running container with fixed ten-second timeout | `Docker` / Destructive |
| `docker.container_restart` | Restart one exact running container with fixed ten-second timeout | `Docker` / Destructive |
| `docker.container_pause` / `resume` | Pause running or resume paused exact container | `Docker` / Destructive |
| `docker.container_remove` | Remove one exact stopped standalone container, without force or volumes | `Docker` / Destructive |

Do not expose a generic `docker exec <string>` tool. An interactive shell is a
real hosted Terminal panel and receives the terminal tool set, input lease,
screen state, waits, and audit behavior. Do not expose the current multi-container
stack loop initially: partial success across independently committed container
actions needs a separate batch receipt design.

Force/volume removal, prune, build, pull, push, copy-in, and copy-out are absent
until typed clients support them with bounded progress and exact receipts.

An opt-in live-daemon integration test exercises the production command client
and hosted session against one random disposable container. Set
`ASURA_RUN_DOCKER_LIFECYCLE_INTEGRATION=1` and
`ASURA_DOCKER_LIFECYCLE_IMAGE` to an already-present image with a
long-running default command. The test uses `--pull=never`, validates the exact
full container ID, start/restart/pause/resume/stop/remove state transitions and
governed receipts, and cleans only its generated container in `finally`.

## Policy capability changes

Append, never reorder, the following capabilities in `AgentCapability`:

| Capability | Purpose | Default |
| --- | --- | --- |
| `BrowserScripting` | JavaScript evaluation in an exact browser document | Off |
| `BrowserDiagnostics` | console/network/CDP diagnostics | Off |
| `DatabaseRead` | bounded relational and Redis observations | Off |
| `DatabaseWrite` | SQL/row/Redis mutations | Off |
| `DockerData` | Docker observations distinct from lifecycle control | Off |
| `SystemData` | aggregate local statistics | Off |
| `ProcessData` | bounded local process listing | Off |
| `ArtifactTransfer` | browser upload/download and cross-panel artifact movement | Off |

Existing durable payload shapes remain readable but every absent new capability
fails closed. A deliberate migration/UI edit is required to enable one. Keep
`Docker` for lifecycle mutation and `ProcessControl` for any future process
mutation; do not overload observation and control under one permission.

## Implementation status and remaining sequence

### Landed foundation

1. Add the new policy capabilities with fail-closed schema compatibility.
2. Add `browser.agent_input_barrier` and browser input-lease/epoch support.
3. Define and host Database, Redis, and Docker session contracts.
4. Enforce strict bounded JSON projections and opaque references across every
   newly exposed observation.

The run-scoped artifact broker remains a prerequisite for binary screenshots,
uploads, downloads, and non-text file previews.

### Landed: terminal completion

1. Add non-mutating bounded scrollback projection.
2. Wire native full-scrollback search.
3. Expose hosted viewport scrolling and prompt/command wait conditions.
4. Add parser/composer/host/result tests following existing terminal patterns.

This phase is low architectural risk because the typed terminal ports and
libghostty-vt state already exist.

### Landed subset: CEF semantic browser

1. Implement CEF accessibility snapshot and opaque backend-node leases.
2. Add bounded wait with delay/load/URL/text/ref/revision/network conditions.
3. Implement click/fill/ensure-checked with acknowledged real input and exact
   post-action verification.
4. Add atomic panel-relative mouse move/click/wheel, key press, and scroll under
   the browser input barrier.

### Remaining: CEF interaction and power tier

1. Add getters/predicates, screenshot artifacts, type/select/focus/hover, and
   split or multi-event drag input with explicit commit receipts.
2. Define a credential-safe scripting boundary; do not promote the current
   arbitrary-evaluate candidate merely because it uses an isolated world.
3. Add redacted console/network buffers.
4. Add the versioned CDP method allowlist.
5. Add artifact-backed upload and download.

### Landed subset: hosted Database, Redis, and Docker

1. Link the hosted projection after the real panel graph is accepted without
   changing eager human initialization.
2. Add read-only tools and conformance first.
3. Add one exact mutation at a time with commit and outcome-unknown tests.

### Remaining: File Viewer mutations and artifacts

1. Add transfer cancellation/retry only after the queue proves final state and
   commit evidence.
2. Add exact cross-panel copy; same-provider move/rename is already governed,
   while copy-then-delete move needs explicit partial-effect evidence.
3. Add ACL writes only for providers with race-safe version semantics.

## Verification gates

Every tool family needs:

- schema tests for exact and broad scope;
- capability advertisement/removal tests;
- parser rejection of duplicate/unknown/oversized fields;
- composer digest and approval-presentation tests;
- host target/revision drift tests adjacent to permit consumption;
- cancellation before and after commit;
- human-input preemption for terminal/browser input;
- bounded-output and hostile-Unicode/content tests;
- no-retry tests after disconnect/timeout/invalid receipt;
- renderer/session replacement and close races; and
- provider continuation tests for success, ordinary failure, and
  outcome-unknown quarantine.

Browser additionally needs deterministic local fixtures for accessibility,
shadow DOM, iframes/OOPIF, canvas, contenteditable, file input, downloads,
redirects, SPA navigation, dialogs, renderer crash, prototype poisoning, and
input ordering. Public websites are not conformance fixtures.

Database mutation tests use disposable databases and induced post-dispatch
disconnects. Docker tests use disposable containers and prove exact resource
identity across refresh. File transfer tests cover provider-generation drift
and partial move. Terminal tests replay recorded VT fixtures for shells, REPLs,
alternate-screen TUIs, mouse tracking, bracketed paste, and OSC 133 boundaries.

## Rejected alternatives

- **One generic `panel.invoke` tool.** It destroys capability-specific schemas,
  risk classification, least-privilege advertisement, and provider clarity.
- **UI automation against Avalonia controls.** It races presentation state and
  bypasses the engine/session authority that the user actually cares about.
- **Desktop-wide mouse and keyboard tools.** They can escape the target panel
  and operate other applications.
- **Terminal command execution beside the PTY.** It would not operate the
  interactive session the user sees and would diverge on SSH, Docker, WSL,
  REPLs, and TUIs.
- **Selectors as browser identity.** Selectors can retarget after DOM changes;
  opaque snapshot-bound backend-node leases fail stale instead.
- **Page-realm scripts for every browser action.** They are weaker against
  hostile prototype changes and generate less realistic input than CEF/CDP.
- **Unrestricted CDP by default.** It collapses navigation, data, interaction,
  filesystem, network-secret, and target-lifecycle permissions into one ambient
  escape hatch.
- **Agent tools on Database/Docker view models.** Presentation does not own
  authorization, exact target resolution, durable audit, or mutation receipts.
