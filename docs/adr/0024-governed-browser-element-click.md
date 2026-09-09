# ADR 0024: Governed browser element click

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0020](0020-native-webview-wrapper-and-first-browser-capability-slice.md),
  [ADR 0021](0021-governed-browser-state-and-navigation.md),
  [ADR 0022](0022-governed-browser-origin-containment.md),
  [ADR 0023](0023-governed-native-document-snapshots.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)
- Extended by:
  [ADR 0025](0025-governed-browser-element-fill.md)

## Context

ADR 0023 returns random opaque references for actionable nodes in a bounded
top-document snapshot. The first consuming operation must activate the exact
element that produced a reference, not rerun a sibling-index locator that can
retarget after DOM reorder. It must also reject in-document mutation, document
replacement, reference replay, and provider-forged handles without giving the
provider JavaScript, selectors, DOM objects, or a native-webview object.

Activation is a page mutation. It may submit data, change application state, or
start top-level navigation before a portable native script call returns.
Cancellation and native failure therefore need an explicit dispatch-commit
boundary: once activation may have occurred, Asura must not report a safe
cancellation or retry an uncertain effect.

## Decision

Asura adds `browser.click` as the eighth governed browser tool. It is a
trusted mutation under a distinct `BrowserInteraction` capability, separate
from `BrowserData` observations and `BrowserNavigation` mutations. Its default
permission is `Ask`. The broker binds one approval to the exact provider-visible
reference and non-negative document revision. The session-host domain gate
accepts `HumanApproval` or explicitly confirmed run-local `YoloPolicy` for
click; `AutoPolicy` and every other source fail closed.

Click remains in ADR 0026's explicit full-automation candidate profile. The
production desktop does not advertise it while the exact-object registry and
synthetic activation execute in a poisonable page realm.

The closed provider schema accepts only:

- one URL-safe opaque `reference` of at most 128 bytes; and
- one non-negative `document_revision`.

The provider supplies neither an address nor a selector. The trusted composer
includes both arguments in the material-argument digest and narrows a broad
run to one exact browser panel/session before approval. After consuming the
one-action authorization, SessionHost revalidates the exact current interactive
attachment owned by the approving client, the `browser.click` and
`browser.navigation_origin_guard` capabilities, ready load state, and the
provider revision against the current committed document. It freezes that
document's address and origin, then repeats the host policy check immediately
before dispatch.

`BrowserPanelSession` validates the logical committed address and revision,
translates the reference to the exact last-projected renderer-local document,
and requires the successful receipt to translate back to the original logical
source document. Renderer replacement, an unprojected renderer change, a
revision regression, address drift, or a mismatched receipt fails closed.

### Exact-object reference registry

Snapshot, click, and the fill consumer added by ADR 0025 use fixed,
application-owned scripts inside the existing native webview. The snapshot
script creates one private page-realm registry under a random slot and secret.
For each actionable node it stores:

- the exact `HTMLElement` object in a private map;
- a random snapshot nonce and bounded element token;
- the current `MutationObserver` epoch; and
- a validation closure for document ownership, connection, namespace, tag,
  input type, role, accessible name, state, visibility, and enabled state.

The native parser accepts only a nonce/token/epoch handle with closed bounded
shapes. The public random `be_...` reference maps privately to that handle, the
exact native adapter, the exact document binding, and its two-minute expiry.
No selector or native handle crosses the application or provider boundary.

The observer watches top-document subtree, child-list, attribute, and character
data changes. Snapshot capture flushes pending records and succeeds only when
its start and finish epochs match. Click flushes records again and requires the
registry secret, current nonce, exact token, and captured epoch. Any observed
intervening DOM mutation clears the registry and returns a stale reference.
The validation closure then rechecks the same exact object; there is no
`querySelector`, locator replay, or sibling-index resolution.

An accepted click attempt is one-shot. Before native activation, the surface
invalidates every public lease from that snapshot. Inside the registry,
activation obtains the exact stored object and clears the complete native map
before calling it. Success, staleness, non-interactability, cancellation after
acceptance, or ambiguity cannot make the reference reusable.

### Activation, navigation, and completion

The fixed click script calls the captured page-realm
`HTMLElement.prototype.click` on the exact stored object. Provider-supplied
JavaScript, raw script evaluation, DOM/native objects, CDP, Node.js, and a
separate browser controller remain outside the public path.

Only one click, fill, snapshot, or governed navigation may be active on a
browser surface. A click starts only from a ready document whose committed
address is inside the frozen origin. Any synchronously observed top-level
navigation start attributable to the click is checked before the renderer
accepts it. A start outside the frozen origin or to an unsupported scheme is
cancelled synchronously. Same-origin navigation remains allowed, but click
completion waits for its terminal native event and final-address check; a
successful commit advances the document revision. A click with no navigation
returns only a source-document receipt.

Cancellation retains authority until immediately before native dispatch. If it
wins there, the operation is reported cancelled and no click is issued. Once
the native activation call is committed, later caller, attachment, session, or
policy cancellation cannot overwrite a confirmed result or relabel the action
as safely cancelled. The host never redispatches the click during completion
audit or state reconciliation.

A malformed native result, thrown activation, deadline, missing or conflicting
navigation terminal event, post-dispatch cancellation exception, mismatched
receipt, or otherwise ambiguous completion returns the non-retryable stable
code `browser_interaction_outcome_unknown`. Native-surface ambiguity and
cross-origin navigation denial invalidate references and attempt to quarantine
the old adapter. When replacement succeeds, Asura installs a fresh
`about:blank` adapter and advances the document revision; late callbacks remain
confined to the old adapter. If adapter, dispatcher, or receipt recovery cannot
be confirmed, the surface remains unavailable rather than permitting another
interaction.

Every `browser_interaction_outcome_unknown` is committed as a non-retryable
failed tool result. Asura skips the remainder of the stale provider batch,
then lets the provider inspect the replacement/current browser state before it
chooses another action. The native adapter may remain quarantined or
unavailable when recovery cannot be confirmed, but ordinary interaction
uncertainty does not destroy the conversation or revoke unrelated run
authority.

Interaction-specific provider-visible stable codes include
`browser_element_reference_stale`,
`browser_element_not_interactable`,
`browser_interaction_outcome_unknown`,
`browser_state_changed`, and
`browser_domain_policy_denied`; renderer messages and arbitrary native codes do
not cross the stable-code allowlist.

## Consequences

- A provider-visible reference can activate only the exact element object held
  by the snapshot registry for the exact current document.
- Any observed in-document mutation conservatively makes the complete snapshot
  reference set stale, trading availability for non-retargeting.
- Click policy is independently configurable in the model, while the host
  still requires one exact human approval as defense in depth.
- Reference replay, ambiguous retries, and late native completion fail closed.
- Same-origin top-level navigation caused by a click is contained and audited
  as part of that one click action.

## Limits and required evidence

- Registry, observer, validation, and activation execute in the page realm.
  Random private names and strict native parsing prevent provider handle
  injection, but they are not an isolated browser world. A hostile page may
  poison `Map`/`Set`, `Function.prototype.call`, other realm-visible APIs, or
  prototypes before or after registry installation and snapshot capture.
- Activation uses synthetic `HTMLElement.prototype.click`. It is not a trusted
  user gesture, pointer sequence, coordinate hit test, or proof that another
  element does not visually cover the target. Browser user-activation,
  popup, permission, focus, and default-action behavior may differ.
- The registry covers actionable `HTMLElement` objects in the top document
  only. Frames, shadow roots, platform-native accessibility nodes, non-HTML
  elements, and new-window flows are not implemented by this slice.
- Origin containment depends on synchronously delivered top-level native
  navigation events and the same portable generation limits described by
  ADR 0022. It does not govern subresources, service workers, downloads, or
  page side effects that do not navigate. A delayed asynchronous navigation
  after confirmed click completion is a later page action, not part of the
  completed click transaction.
- WKWebView, WebView2, WPE WebKit, and any selected WebKitGTK fallback still
  require named-platform conformance for registry integrity, mutation
  observation, synthetic activation, event ordering, quarantine, and late
  completion before cross-platform click support is considered complete.
- Bounded fill is added by ADR 0025. Double-click, hover, focus, general type,
  select, check, press, scroll, waits,
  screenshots, and additional reference consumers remain separate slices.

## Alternatives rejected

- Replaying a structural or sibling-index locator can activate a different
  element after in-document reorder.
- Giving the provider selectors or JavaScript makes executable page authority
  open-ended and bypasses the closed tool catalog.
- Keeping leases reusable after an attempt permits replay of non-idempotent page
  actions.
- Reporting post-dispatch cancellation as safe encourages a retry when the
  original click may already have executed.
- Retrying `browser_interaction_outcome_unknown` can duplicate submissions or
  other irreversible page effects.
- A Node.js/CDP controller or bundled browser sidecar bypasses the user's
  attached native-webview session and adds a second runtime.
