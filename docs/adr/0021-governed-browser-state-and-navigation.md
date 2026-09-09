# ADR 0021: Governed browser state and navigation

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0019](0019-one-action-agent-capability-broker.md),
  [ADR 0020](0020-native-webview-wrapper-and-first-browser-capability-slice.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)
- Extended by:
  [ADR 0022](0022-governed-browser-origin-containment.md),
  [ADR 0023](0023-governed-native-document-snapshots.md),
  [ADR 0024](0024-governed-browser-element-click.md),
  [ADR 0025](0025-governed-browser-element-fill.md),
  [ADR 0026](0026-native-browser-capability-conformance-gate.md),
  [ADR 0027](0027-governed-browser-element-check.md)

## Context

ADR 0020 established an engine-neutral embedded-browser boundary and a small
human-operated browser capability set. The agent needs a first useful browser
slice without gaining a vendor browser object, raw JavaScript, DOM authority, a
Chrome DevTools Protocol escape hatch, or an additional Node.js process.

[ADR 0042](0042-cef-off-screen-browser-runtime.md) later replaces the native
webview implementation with CEF OSR while preserving this governed contract.

Browser state is page-controlled data, while navigation changes an
authenticated browser context. Both therefore need the same exact target,
one-action authorization, cancellation, and completion-audit guarantees as
terminal tools, plus browser-specific attachment and origin checks.

## Decision

Asura implements ten closed governed browser tool contracts in this slice:

- `browser.read_state`;
- `browser.snapshot`;
- `browser.click`;
- `browser.fill`;
- `browser.check`;
- `browser.navigate`;
- `browser.back`;
- `browser.forward`;
- `browser.reload`;
- `browser.stop`.

`browser.read_state` and `browser.snapshot` are observations under
`BrowserData`. The five navigation operations are mutations under the separate
`BrowserNavigation` capability. `browser.click`, `browser.fill`, and
`browser.check` are
mutations under the separate `BrowserInteraction` capability.

The application owns a closed typed request union and a trusted browser-action
composer. The composer derives the executable request, exact material-argument
digest, and approval presentation from the same typed value. An explicit
navigation binds the complete bounded URL; provider-defined operation names or
argument shapes cannot extend the set.

The broker evaluates trusted risk before the browser-specific host gate. All
five `BrowserNavigation` tools are cataloged as mutations, so `Auto` escalates
them to an exact `HumanApproval`; setting `BrowserNavigation=Auto` does not make
navigation approval-free. `browser.read_state` and
`browser.snapshot` are the operations that can normally receive an `AutoPolicy`
authorization. Click, fill, and check are cataloged as mutations, and the broker
escalates `BrowserInteraction=Auto` to an exact `HumanApproval`. The host still
handles every authorization source as defense in depth: all three interactions
accept `HumanApproval` or explicitly confirmed run-local `YoloPolicy` and
continue to reject `AutoPolicy`.

For an exact panel or connection-session target, the provider schema omits
`panel_id`; the host-owned target supplies that identity. For a broader
internal `OpenTab` target or Workspace target, every browser schema requires
`panel_id`, even when only one browser is currently eligible. Its enum contains
only fresh active browser panels that support that operation. The runtime
parses against a fresh resolution and the composer narrows the action to one
exact panel/session before approval.

Workspace and internal `OpenTab` runs pin their enclosing identity rather than
their initial mixed panel membership. The runtime refreshes eligible browser
topology and rebuilds tool schemas between provider rounds. Once a proposal
selects a current browser, its narrowed panel/session and attachment binding is
freshly revalidated through authorization and dispatch; disappearance,
replacement, or capability loss during that action fails closed. Exact and
selected-terminal targets remain internal fixed-membership contracts, and the
selected variant remains terminal-only. Workspace is the only visible desktop
scope.

The session host requires one exact current interactive browser attachment
owned by the authenticated desktop client that approves the action. It captures
the attachment identity and cancellation authority, consumes the broker's
expiring authorization once, then revalidates the session revision, attachment
ownership, scope, typed arguments, and browser capability before dispatch.
Browser operations do not acquire or imply a terminal input lease.

The ordinary browser-session API remains a human chrome path. It requires the
exact interactive `Human` actor/client and does not accept an `Agent` actor as a
shortcut around the broker. Agents use only the governed browser bridge.

The current first-party CEF boundary exposes URL admission but neither the
actual connected socket peer nor a request-context proxy that Asura can
bind to one browser action. Repeating DNS resolution before CEF dispatch does
not close that check/use gap. Consequently every model-governed CEF navigation,
history movement, reload, element mutation, and low-level automation request
fails closed with `NavigationPolicyDenied` before native dispatch. Detached CEF
web-read and web-search execution likewise returns a closed destination denial
before constructing a CEF browser. Because no governed request is dispatched,
redirect and subresource requests cannot be created by these paths. Human
browser chrome remains a separate explicit path. SSH-routed governed HTTP(S)
also fails closed: remote
loopback, link-local, private, and metadata access requires a future separate
exact capability and approval plus remote-peer enforcement. A future transport
may re-enable these tools only when it preserves hostname TLS validation while
binding policy to every actual peer.

Every consumed authorization receives exactly one `succeeded`, `failed`, or
`cancelled` completion through the existing broker audit and quarantine
mechanism. A completion retry never redispatches the browser operation. Once
the native engine reports a mutation successful, late caller cancellation or a
best-effort state-reconciliation failure does not reverse that reported effect;
changed attachment authority before dispatch still fails closed.

Provider-visible state is a bounded sanitized projection labeled
`content_origin=untrusted_browser`. It includes the trusted panel ID, load and
history state, document revision, a secret-redacted and UTF-8-bounded title, and
an HTTP(S) address without query or fragment. Renderer failure messages do not
cross the boundary; only closed stable codes and retryability do. Mutation
success returns a receipt rather than page content.

`browser.snapshot` follows the same trust boundary with the additional exact
document, capture, serialization, and reference-lifetime controls in
[ADR 0023](0023-governed-native-document-snapshots.md).
`browser.click` consumes one reference through the fixed exact-object registry,
mutation epoch, one-shot lease, and outcome-unknown controls in
[ADR 0024](0024-governed-browser-element-click.md).
`browser.fill` consumes the same kind of reference for a deliberately narrow
set of text controls, binds raw bounded non-secret text to the digest, presents
it through a reversible quoted/escaped approval value, and never
echoes that text in provider results or audit, as recorded in
[ADR 0025](0025-governed-browser-element-fill.md).
`browser.check` consumes the same exact-object reference to ensure native
checkbox/radio checkedness, with already-checked no-op success and native
activation verification, as recorded in
[ADR 0027](0027-governed-browser-element-check.md).

The production capability profile advertises read-state, guarded navigation,
and stop, but the shipped CEF renderer denies the governed mutation at dispatch
because it cannot attest the transport peer. Snapshot, click, fill, and check
remain in the explicit full-automation candidate profile until the named native adapter satisfies
[ADR 0026](0026-native-browser-capability-conformance-gate.md).

### Browser action authorization

The host evaluates this policy after consuming the one-action authorization and
again immediately before renderer dispatch:

| Authorization source | Host decision |
|---|---|
| `HumanApproval` | The approval is the one-use allow decision for that exact typed browser action and its bound arguments. |
| `AutoPolicy` + `read_state`, `snapshot`, `wait`, `navigate`, `reload`, or `stop` | Accepted by the host. Under the current broker, mutation risk still escalates navigation and reload to `HumanApproval`, so this is defense in depth rather than an approval bypass. |
| `AutoPolicy` + `click`, `fill`, or `check` | Denied. Browser interaction requires exact human approval even if the configured `BrowserInteraction` permission is `Auto`. |
| `AutoPolicy` + `back` or `forward` | Denied because this slice cannot determine the history destination origin before dispatch. |
| `YoloPolicy` | Allowed only for the explicitly confirmed live run and still subject to the exact typed request, starting document/revision, reference, input-barrier, and session checks. |

An authorization-source denial uses the stable code
`browser_action_not_authorized` and is completion-audited as a failed consumed
action. The host does not impose a same-origin browsing policy: an authorized
navigation may move between any addresses accepted by `BrowserAddress`,
including leaving `about:blank` or following a cross-origin link.

This decision originally governed only the requested top-level operation.
[ADR 0022](0022-governed-browser-origin-containment.md) adds one-action
top-level redirect containment and final-completion auditing. A complete
browser allowlist and named-platform conformance evidence remain separate.

[ADR 0023](0023-governed-native-document-snapshots.md) adds the governed
fixed-script document projection, exact document binding, provider byte bound,
and short-lived opaque references.

[ADR 0024](0024-governed-browser-element-click.md) adds one human-approved
exact-object reference consumer with conservative DOM-mutation invalidation,
starting-document binding, and non-retryable outcome-unknown quarantine.

[ADR 0025](0025-governed-browser-element-fill.md) adds a second
human-approved, exact-object reference consumer for bounded non-secret text,
with a narrow fillable-control allowlist and the same one-shot,
outcome-unknown quarantine rule.

[ADR 0027](0027-governed-browser-element-check.md) adds a third
human-approved, exact-object reference consumer that ensures native
checkbox/radio checkedness without a toggle retry hazard.

No Node.js process, bundled Chromium controller, or CDP client is launched.
Execution remains in the existing embedded-browser session through typed
application and session-host ports.

## Consequences

- Production agents can read state and inspect already-loaded content through
  bounded observation contracts. With the shipped CEF adapter they cannot
  navigate, interact with elements, dispatch low-level input/evaluation, or use
  detached rendered web-read/search because those paths fail before native
  dispatch. Ordinary human browsing remains available.
- Browser data and navigation can be configured and audited independently.
- Broad Workspace and internal `OpenTab` scopes remain explicit at the provider
  schema, refresh eligible topology between rounds, and are narrowed to one
  freshly bound exact panel/session before approval.
- Automatic navigation is deliberately conservative when the destination
  origin cannot be known, even though the current broker already escalates all
  browser navigation mutations to exact human approval.
- The result boundary treats browser-controlled text and state as untrusted and
  does not expose renderer diagnostics.
- Double-click, hover, focus, general typing, select/uncheck, press/scroll, waits,
  screenshots, provider-authored JavaScript, profiles, permissions, downloads,
  dialogs, certificates, and cross-platform automation conformance remain
  future slices.

## Alternatives rejected

- Giving the agent the ordinary human browser API would make actor identity a
  policy bypass.
- Omitting `panel_id` when a broad scope happens to contain one browser would
  make the provider schema change silently as live membership changes.
- Allowing automatic history traversal cannot enforce an origin rule without a
  typed history-destination preview.
- Treating browser navigation as terminal input would grant irrelevant input
  lease authority and couple independent resources.
- Launching a Node.js/CDP controller adds a second runtime and bypasses the
  embedded-browser boundary preserved by ADR 0042.
