# ADR 0029: Scope-clipped governed workspace-graph observations

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0016](0016-host-owned-runtime-workspace-graph.md),
  [ADR 0019](0019-one-action-agent-capability-broker.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

The built-in agent needs to understand the tabs and panels that the user has
already placed inside a run's target. Giving it an ambient workspace browser
would widen exact panel, connection-session, `OpenTab`, and selected-panel scopes,
leak sibling topology, and turn provider-chosen identifiers into discovery
authority.

The host graph also changes for two different reasons. Membership, order,
ownership, and panel kind are authorization-relevant structure for one action;
titles, focus, visibility, lifecycle, graph revision, and sequence are
observations. Workspace and `OpenTab` targets deliberately accept a new live
eligible topology between provider rounds while retaining their enclosing
identity. Exact and selected targets retain fixed membership. Treating all
graph change as either permanently pinned or permanently fluid would therefore
reject legitimate Workspace evolution or accept unsafe action retargeting.

## Decision

Asura exposes three closed read-only tools through the native governed
runtime:

- `workspace.inspect`;
- `tab.list`;
- `panel.list`.

They are `Search` observations. Each proposal passes through the capability
broker, receives one exact authorization, is consumed once by SessionHost, and
has a complete durable audit outcome. The provider adapter has no graph client
and the agent runtime cannot call the host without this path.

### Scope clipping

The host resolves the original immutable `AgentTarget` and projects only graph
objects already inside it. A panel or current connection-session scope contains
one exact current graph panel. An internal `OpenTab` scope contains that exact
tab's current panels. A Workspace scope contains that one exact workspace. An
internal selected-panel scope contains only the exact selected set. Every run
is already bound to one workspace by trusted host context, so
`workspace.inspect` returns that workspace and there is no workspace discovery
tool. Workspace is the only target choice exposed by the current desktop UI;
the other variants remain closed internal/testable contracts.

Graph observations include registered Terminal, Browser, File Viewer,
Statistics, and Process Monitor panels. They require every in-scope panel to be
registered in one consistent graph snapshot. A graphless Quick Terminal or
other graphless connection session advertises none of these tools.

### Model-controlled input

`workspace.inspect` accepts only `{}`. `tab.list` and
`panel.list` accept an optional `offset` from the fixed set `0`, `16`, `32`,
and `48`; omission means zero and the page size is always 16. Duplicate,
unknown, fractional, negative, and out-of-range fields fail before
authorization.

The model cannot provide a window, workspace, tab, panel, session, query,
filter, sort, total, page size, continuation token, or projection field. A page
receipt reports its fixed offset and page size, returned in-scope item count,
bounded next offset, completion flag, and items. It exposes no total or
out-of-scope count from which sibling topology could be inferred.

### Structural binding and refresh

Preparation for one graph action binds its current ordered, scope-relative
sequence of `window/workspace/tab/panel/kind` identities. SessionHost
reconstructs the same clipped graph while holding its graph gate, consumes the
one-action permit, and compares the fresh structural binding before projection.
Addition, removal, reordering, ownership change, or kind change during that
one-action authorization window fails closed as `target_changed`.

Across provider rounds, Workspace and internal `OpenTab` targets keep only
their exact enclosing identity pinned. The runtime re-inspects their current
eligible topology, replaces its context projection, and rebuilds contributed
tool schemas. A graph change completed before that refresh is accepted as the
new action-preparation baseline; it does not require a new run. Exact panel,
connection-session, and selected-set targets instead keep their complete
ordered structural binding for the run and fail closed on membership drift.

Raw global tab and panel ordinals are deliberately absent from this binding.
Adding or reordering a sibling outside an exact or otherwise clipped scope
neither invalidates the action nor leaks that sibling. A connection-session
target additionally requires that the panel still owns that exact current
session, so supersession cannot reveal the replacement panel shell.
Workspace revision and graph sequence are omitted from every clipped provider
projection because they are global clocks that would otherwise disclose
unrelated sibling activity. They are returned only when the run target is the
complete workspace.

For exact and selected targets, the governed runtime pins the structural
sequence independently from operational session state. For Workspace and
`OpenTab`, it retains the per-action sequence only long enough to prevent a
time-of-check/time-of-use retarget, then accepts the next round's freshly
resolved sequence. Title, focus, visibility, lifecycle, workspace revision,
and graph-sequence refreshes remain non-structural within one action. Graph
observation can therefore continue while an otherwise unchanged session moves
from `Active` to `Starting` or `Closing`; terminal, browser, and File Viewer
operations still require their exact usable operational binding and fail
closed.

### Bounded hostile metadata

Results contain only host-owned IDs, panel kind, focus/visibility, bounded
titles, and—for a complete workspace target only—workspace revision and graph
sequence. They contain no session ID, connection, capability, address, current
directory, browser, File Viewer, process, terminal content, or reusable permit
data.

Titles are untrusted. Secret-shaped or unsafe-Unicode titles are replaced
before continuation; accepted text is valid Unicode and is truncated
rune-safely to 128 UTF-8 bytes with explicit redaction/truncation metadata.
Every result carries
`content_origin=untrusted_workspace_graph_metadata`, its exact scope kind, and
whether that scope is clipped. The application projection and the actual
serialized JSON are each limited to 64 KiB. Raw titles and graph content are
excluded from durable audit.

## Consequences

- The agent can reason about the user's current layout, including non-session
  panels, without acquiring ambient workspace discovery.
- Exact and selected scopes remain stable across unrelated sibling changes and
  presentation-only refreshes. Workspace and `OpenTab` retain enclosing
  identity while accepting current eligible topology at round boundaries.
- Structural drift during one action, fixed-target membership drift, session
  supersession, missing graph registration, malformed input, and oversized or
  unsafe output fail closed.
- Fixed paging covers at most the first 64 tabs or panels. Search, filters,
  arbitrary continuation, cross-window discovery, and graph mutation remain
  outside this observation slice.

## Alternatives rejected

- Returning the complete desktop graph would widen every target below the
  window and disclose unrelated workspaces, tabs, and panels.
- Accepting provider-supplied IDs or filters would turn untrusted data into
  discovery authority.
- Binding global ordinals would make out-of-scope sibling changes observable
  through action failure.
- Binding revision, sequence, titles, focus, visibility, or lifecycle as
  structure would make harmless refreshes terminate otherwise valid runs.
- Omitting one-action authorization because the tools are read-only would
  bypass policy, revocation, and durable evidence for information disclosure.
