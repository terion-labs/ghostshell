# ADR 0023: Governed native document snapshots

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0020](0020-native-webview-wrapper-and-first-browser-capability-slice.md),
  [ADR 0021](0021-governed-browser-state-and-navigation.md),
  [ADR 0022](0022-governed-browser-origin-containment.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)
- Extended by:
  [ADR 0024](0024-governed-browser-element-click.md),
  [ADR 0025](0025-governed-browser-element-fill.md)

## Context

An agent needs a bounded description of the document visible in the user's
attached native-browser panel before reference-based interaction can be added.
Giving a provider arbitrary JavaScript, a DOM object, a vendor webview object,
or a CDP/Node.js controller would bypass the typed application boundary chosen
by ADR 0020.

The portable native-webview contract also has no uniform accessibility-tree
snapshot API. A first cross-platform slice therefore needs an application-owned
capture mechanism, exact document binding, hostile-page parsing, bounded
provider serialization, and short-lived references without presenting that
mechanism as a complete named-platform accessibility implementation.

## Decision

Asura adds `browser.snapshot` as an observation under `BrowserData`.
Snapshot proposals use the same exact target resolution, interactive attachment
ownership, one-action authorization, cancellation, and completion-audit path
established by ADR 0021. [ADR 0024](0024-governed-browser-element-click.md)
later adds the eighth governed browser tool, `browser.click`, as a mutation
under the separate `BrowserInteraction` capability.
[ADR 0025](0025-governed-browser-element-fill.md) adds the ninth,
`browser.fill`, under that same interaction capability.
[ADR 0027](0027-governed-browser-element-check.md) adds the tenth,
`browser.check`, under that same interaction capability.

The public tool accepts no script. The native adapter alone owns one fixed,
private document-capture script. No provider-supplied JavaScript, raw
JavaScript operation, DOM or native-webview object, CDP client, Node.js process,
or separate browser controller crosses the application boundary.

SessionHost binds capture to the exact trusted committed address and logical
document revision. `BrowserPanelSession` validates that logical binding,
translates it to the exact last-projected renderer-local address and revision,
and translates a successful result back to the logical document. The renderer
checks the binding immediately before and after native capture. Address or
revision drift fails closed, and a renderer revision regression invalidates
the current projection and reference leases instead of being normalized into a
plausible newer logical document.

The native capture is limited to the current top document and accepts at most
128 derived nodes. The fixed script and the native parser independently bound
traversal, depth, text, labels, locators, states, decoded bytes, and structural
shape. The parser rejects malformed, duplicate, oversized, or inconsistent
page-controlled data. This is a DOM-derived page-realm projection, not a
platform-native accessibility tree, and it does not claim frame, shadow-root,
or named-platform parity.

Provider output remains `content_origin=untrusted_browser`. Serialization is
measured after JSON escaping and is at most the provider tool-result limit of
64 KiB; the runtime reduces the projected node count until the actual encoded
envelope fits. Secret-shaped page text is redacted, HTTP(S) query and fragment
data are removed, overlong addresses are truncated with explicit metadata, and
only a closed allowlist of stable browser error codes can cross the provider
boundary. Renderer messages and arbitrary provider/native error codes do not.

Each returned element reference is a cryptographically random opaque value
bound privately to the exact document, native adapter, and private native
handle. A reference expires after two minutes, on the next snapshot, on
navigation or document revision, on adapter replacement, on session detach or
close, or when the owning browser surface is otherwise discarded. ADRs 0024,
0025, and 0027 permit `browser.click`, `browser.fill`, or `browser.check` to consume one reference only
by resolving that lease to the exact page-realm element object and matching its
captured `MutationObserver` epoch. The public reference carries neither
selector nor native-handle semantics.

Only one native snapshot may be outstanding per browser surface. Capture has a
bounded deadline and observes caller/session cancellation. Because the portable
native script invocation itself is not cancellable on every engine, a cancelled
capture remains the one outstanding operation until it drains, and every late
completion is fenced by adapter identity, document binding, and reference
epoch. A deadline that leaves native completion ambiguous quarantines the old
adapter and attempts fail-closed replacement before later work can proceed.
Snapshot capture is unavailable while navigation is unresolved.

## Consequences

- The full-automation candidate profile can inspect a bounded projection of the
  same committed top document the user sees without a second browser runtime.
  Production does not advertise snapshot until ADR 0026's native conformance
  gate is satisfied.
- Snapshot data and navigation retain separate capability and risk policy.
- Actual encoded provider output, rather than a pre-escaping estimate, enforces
  the 64 KiB boundary.
- Opaque references alone grant no interaction authority. ADRs 0024, 0025,
  and 0027 combine one with an exact human-approved click, bounded
  text-control fill, or native checkbox/radio check, unchanged mutation epoch,
  and one-shot native lease.
- Renderer or native-capture ambiguity revokes the projection or quarantines
  the adapter instead of guessing that a late result is current.

## Limits and required evidence

- The capture executes a fixed script in the page realm and reads only the top
  document. Hostile pages may poison realm-visible built-ins and prototypes
  before or after registry installation and capture; independent parser limits
  and document revalidation contain malformed results but do not provide an
  isolated world or native accessibility tree.
- WKWebView, WebView2, WPE WebKit, and any selected WebKitGTK fallback still
  require named-platform conformance evidence for page-realm behavior,
  deadline/cancellation ordering, adapter quarantine, Unicode bounds, and
  projection consistency.
- Reference consumers beyond click/fill/check, richer find semantics,
  double-click, hover/focus, general typing, select/uncheck, press/scroll,
  screenshots, profiles,
  permissions, downloads, and the remaining browser automation surface are
  separate slices.

## Alternatives rejected

- Provider-authored JavaScript would turn untrusted model data into executable
  page authority and make review or capability policy open-ended.
- A Node.js/CDP controller or bundled browser sidecar would bypass the selected
  native-webview session and add a second runtime.
- Permanent or predictable element indices would allow stale or guessed
  references to be replayed across documents.
- Trusting only a pre-capture revision check would allow a navigation or adapter
  replacement during capture to relabel stale page data as current.
- Estimating string lengths before JSON serialization would not enforce the
  provider byte limit after escaping.
