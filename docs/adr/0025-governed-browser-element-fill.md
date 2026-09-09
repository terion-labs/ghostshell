# ADR 0025: Governed browser element fill

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0020](0020-native-webview-wrapper-and-first-browser-capability-slice.md),
  [ADR 0021](0021-governed-browser-state-and-navigation.md),
  [ADR 0022](0022-governed-browser-origin-containment.md),
  [ADR 0023](0023-governed-native-document-snapshots.md),
  [ADR 0024](0024-governed-browser-element-click.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

ADR 0023 exposes short-lived opaque references to exact element objects, and
ADR 0024 establishes one-shot reference consumption for click. The agent also
needs to replace the value of a referenced text control without gaining
provider-authored JavaScript, selectors, a DOM or vendor-webview object, a
general keyboard-injection surface, or a second browser runtime.

Fill is a page mutation. Text may be sensitive, page-controlled event handlers
may run synchronously, and setting a value may trigger page state or
top-level navigation. Once the value setter may have executed, cancellation or
native failure cannot prove that the page is unchanged. The operation therefore
needs an explicit commit boundary, non-retryable ambiguous outcomes, and a
narrow control allowlist.

## Decision

Asura adds `browser.fill` as the ninth governed browser tool. It is a
mutation under `BrowserInteraction`, alongside `browser.click`, and its default
permission is `Ask`. The broker escalates `BrowserInteraction=Auto` to an exact
`HumanApproval`; the session-host domain gate independently accepts
`HumanApproval` or explicitly confirmed run-local `YoloPolicy` for fill.
`AutoPolicy` and every other source fail closed.

The closed provider schema accepts exactly:

- one URL-safe opaque `reference` of at most 128 bytes;
- one non-negative `document_revision`; and
- one text value whose strict UTF-8 encoding is at most 2,048 bytes.

The text may be empty, allowing a field to be cleared. Tab, line feed, and
carriage return are permitted; other control characters and unpaired Unicode
surrogates are rejected. Literal secret-shaped material is rejected before an
approval can be requested. This slice deliberately has no password-fill or
opaque browser-secret path.

The trusted composer binds the reference, document revision, and raw exact text
into the one-action material-argument digest. The approval presentation uses a
reversible quoted/escaped rendering so empty, whitespace-only, and permitted
control values remain reviewable without changing what the digest binds. The
provider result contains only a success receipt or stable error, and completion
audit contains only its closed outcome details; neither echoes or persists the
text.

After consuming the authorization, SessionHost revalidates the exact current
interactive attachment owned by the approving client, `browser.fill`,
`browser.navigation_origin_guard`, ready load state, and the provider revision
against the current committed document. It freezes the source address and
origin, then repeats the host policy check immediately before typed dispatch.
`BrowserPanelSession` translates the exact logical source document to its
last-projected renderer-local binding and requires any success receipt to
translate back to that same source document.

### Exact-object and fillable-control boundary

Fill shares the fixed, application-owned page-realm registry established for
snapshot and click. The public reference resolves privately to the exact native
adapter, document binding, snapshot nonce, element token, mutation epoch, and
stored `HTMLElement` object. There is no selector, locator replay,
`querySelector`, provider script, or provider-visible native handle.

An accepted attempt invalidates the complete public reference set. The fixed
script flushes pending mutation records, checks the registry secret, nonce,
token, and exact epoch, obtains the stored object, and clears the complete
native entry set before mutation. It then reruns the captured validation
closure. Stale document ownership, disconnection, changed identity or state,
hidden or inert ancestry, disabled state, or read-only state fails closed and
does not make the lease reusable.

The exact object is fillable only when it is:

- an HTML `<textarea>`; or
- an HTML `<input>` whose normalized type is `text`, `search`, `email`, `url`,
  or `tel`.

Password, file, hidden, number, date/time, checkbox, radio, range, color, and
other input types are excluded. Contenteditable and every non-input element are
also excluded. A wrong element or input type returns the stable
`browser_element_not_fillable` code; an allowed but hidden, inert, disabled, or
read-only control returns `browser_element_not_interactable`.

### Setter, event, navigation, and completion

The native C# adapter builds only its fixed private script and JSON-encodes the
already validated text as data. Before the setter, the script rejects values
that the selected control deterministically normalizes: every input rejects CR
or LF; textarea rejects CR; URL and single-email inputs reject leading or
trailing ASCII whitespace; and multiple-email inputs reject that whitespace
around any comma-delimited token. This known pre-setter failure returns
`browser_fill_value_not_supported`, rather than outcome-unknown. The script
calls the captured platform value setter for the exact input or textarea,
verifies through the captured getter that `element.value` exactly
matches the requested text, and dispatches one bubbling, composed synthetic
`input` event. It does not synthesize keystrokes, a paste gesture, `change`,
focus, blur, or a trusted user activation.

Only one fill, click, snapshot, or governed navigation may be active on a
browser surface. A fill begins only from its exact ready source document inside
the frozen origin. Any synchronously observed top-level navigation start is
subject to the same origin guard as click: unsupported or cross-origin starts
are cancelled, while same-origin navigation must reach its matching terminal
event and final-address check before success.

Cancellation retains authority until immediately before native dispatch. Once
dispatch is committed, the setter or event handler may already have produced an
effect. Late cancellation therefore cannot overwrite a confirmed receipt or be
reported as a safely retryable cancellation. Asura never redispatches fill
during state reconciliation or completion-audit recovery.

A native setter, event-construction, or dispatch failure, mismatched value,
malformed native result, deadline, missing or conflicting navigation terminal
event, receipt mismatch, post-dispatch cancellation exception, or otherwise
uncertain completion returns non-retryable
`browser_interaction_outcome_unknown`. Native-surface ambiguity and
cross-origin navigation denial invalidate references and attempt to quarantine
the old adapter. Successful replacement installs a fresh `about:blank` adapter
and advances the document revision; failed replacement leaves interaction
unavailable. Every outcome-unknown is committed as a non-retryable failed tool
result; the stale remainder of that provider batch is skipped and the provider
must inspect fresh state before choosing another action.

An unexpected exception escaping the in-process host during click or fill is
also normalized to `browser_interaction_outcome_unknown`; it follows the same
settle-and-reconcile continuation path. Observation and navigation host
exceptions retain the separate `browser_host_failed` failure.

No Node.js process, CDP client, bundled Chromium controller, or browser sidecar
is launched. Execution remains in the user's attached native-webview session
through native C# application, session-host, and adapter boundaries.

Fill remains in ADR 0026's explicit full-automation candidate profile. The
production desktop does not advertise it while the page-realm integrity and
session-level ambiguity requirements below remain open.

## Consequences

- A fill can affect only the exact object captured by one current snapshot and
  approved for one exact bounded text value.
- Password controls and literal secret-shaped text remain unavailable until a
  separate opaque browser-secret design is approved.
- Fill reuses the conservative whole-snapshot invalidation and one-shot lease
  rules rather than introducing a second locator or lifetime model.
- Post-commit ambiguity sacrifices adapter availability and forces a fresh
  observation instead of inviting a duplicate mutation or destroying the run.
- Provider results and durable audit prove the action outcome without retaining
  the filled text.

## Limits and required evidence

- Registry validation, value setting, event construction, and event dispatch
  execute in the page realm. Capturing some methods does not create an isolated
  world or close later tampering: a hostile page may poison `Map`/`Set`,
  `Function.prototype.call`, prototypes, or other realm-visible APIs before or
  after registry installation and snapshot capture, potentially defeating
  exact-object or type checks.
- A synthetic `input` event is not trusted keyboard or paste input. Framework
  observation, validation, focus, user-activation, autocomplete, and default
  action behavior may differ from physical typing, and this slice deliberately
  emits neither key events nor `change`. DOM event-listener exceptions are
  generally reported as uncaught rather than propagated from `dispatchEvent`,
  so the fixed script cannot treat every page listener failure as an observable
  outcome-unknown.
- Mutation observation is conservative for observed DOM changes but cannot
  prove every CSSOM, layout, prototype, JavaScript property, or other semantic
  change. Delayed asynchronous navigation after confirmed completion is a
  later page action outside the fill transaction.
- Origin containment depends on synchronously delivered top-level native
  navigation events and their terminal ordering. The portable wrapper cannot
  prove attribution for an interleaved page-initiated same-origin navigation
  without a vendor navigation identifier.
- WKWebView, WebView2, WPE WebKit, and any selected WebKitGTK fallback still
  require named-platform evidence for page-realm integrity, value-setter and
  synthetic-event behavior, navigation-event ordering, cancellation,
  quarantine, and late completion. This ADR does not claim cross-platform fill
  conformance before that evidence exists.
- A custom renderer can report an ambiguous interaction without implementing
  `BrowserSurface` adapter replacement. Session-level ambiguity must reliably
  fence or detach such a renderer before the candidate profile can be enabled.
- Approval currently binds reference, revision, and text. Interaction
  enablement must also bind and show the trusted current origin; presenting the
  snapshotted untrusted role/name would further improve review context.
- Contenteditable, password/secret fill, append/type semantics, keystrokes,
  focus, select/uncheck, and other reference consumers remain separate slices.
  Check was added later by
  [ADR 0027](0027-governed-browser-element-check.md).

## Alternatives rejected

- A provider-supplied selector can retarget after document mutation and does
  not prove identity with the snapshotted object.
- Provider-authored JavaScript or a raw evaluation tool makes executable page
  authority open-ended.
- A Node.js/CDP controller or browser sidecar bypasses the attached native
  webview and adds a second runtime.
- General keyboard injection broadens authority beyond replacement of one
  approved text-control value and creates additional focus and preemption
  races.
- Supporting password fields before an opaque secret-reference path would put
  credentials into provider arguments, approvals, and process memory.
- Retrying an unknown result can duplicate page effects produced by the setter
  or its synchronous event handlers.
