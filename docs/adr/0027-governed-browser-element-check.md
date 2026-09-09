# ADR 0027: Governed browser element check

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0020](0020-native-webview-wrapper-and-first-browser-capability-slice.md),
  [ADR 0022](0022-governed-browser-origin-containment.md),
  [ADR 0023](0023-governed-native-document-snapshots.md),
  [ADR 0024](0024-governed-browser-element-click.md),
  [ADR 0025](0025-governed-browser-element-fill.md),
  [ADR 0026](0026-native-browser-capability-conformance-gate.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

An opaque snapshot reference can identify a native checkbox or radio button,
but `browser.click` cannot express the desired final state. Retrying a click
can invert a checkbox that was already checked. A narrow stateful operation is
therefore useful, provided it does not introduce selectors, arbitrary script,
general property mutation, or a second browser runtime.

Checking a control is still a page mutation. Native activation can run hostile
event handlers, change another radio in the same group, mutate the document, or
start top-level navigation. Once activation begins, failure or cancellation
cannot prove that the page is unchanged.

## Decision

Asura adds `browser.check` as the tenth closed governed browser tool
contract. It is a mutation under `BrowserInteraction`, with default permission
`Ask`. The broker escalates `BrowserInteraction=Auto` to an exact
`HumanApproval`, and the SessionHost domain gate independently accepts
`HumanApproval` or explicitly confirmed run-local `YoloPolicy`. `AutoPolicy`
and every other source fail closed.

The provider schema accepts exactly one URL-safe opaque `reference` of at most
128 bytes and one non-negative `document_revision`. There is no boolean
argument: the tool means ensure checkedness is true. Unchecking remains a
separate future operation and will never uncheck a selected radio implicitly.
The trusted composer binds the exact session, reference, revision, and tool
name into the one-action digest and approval. Provider results and durable
audit contain no page-authored text.

After authorization is consumed, SessionHost repeats exact target,
interactive-attachment, capability, origin-guard, ready-state, document
revision, authorization-source, and current-origin checks. The logical session
translates the exact source document and reference to the last-projected
renderer-local binding and accepts only a receipt that translates back to the
same source document.

### Exact control and activation boundary

The operation consumes the same one-shot, short-lived exact-object reference
used by click and fill. An accepted attempt invalidates the complete public
reference set. The fixed registry flushes pending mutations, validates its
secret, nonce, token, and epoch, obtains the stored exact object, clears every
native entry, and reruns the captured validation closure before activation.

Only a native HTML `<input type="checkbox">` or
`<input type="radio">` is checkable. Custom ARIA checkboxes, switches, other
elements, and other input types return
`browser_element_not_checkable`. Hidden, inert, disabled, disconnected, or
otherwise non-interactable controls fail closed under the existing
interactability boundary.

The registry reads checkedness through the captured native
`HTMLInputElement.prototype.checked` getter. If it is already true, the
operation succeeds without activation or events, while still consuming the
accepted reference. Otherwise it invokes the captured native
`HTMLElement.prototype.click` on that exact object and verifies checkedness
again through the captured getter. This deliberately uses the browser's
checkbox/radio activation behavior: checkbox activation changes checkedness
and clears indeterminateness; radio activation checks the target and may
uncheck its group peer; successful activation fires the browser-defined
`input` and `change` events. It does not synthesize pointer coordinates,
keystrokes, focus, a trusted user activation, or form submission.

Any synchronously observed top-level navigation start uses the same frozen
origin containment as click and fill. Unsupported or cross-origin starts are
cancelled. Same-origin navigation must reach its matching terminal event and
final-address check before success.

A nominal checked result with no observed navigation does not complete in the
same UI turn. Asura posts one navigation-observation barrier while the
pending interaction and frozen-origin guard remain installed. Navigation
events already queued by native activation are therefore handled before
success can commit. Named-platform conformance must still prove the selected
WebView's event ordering before production enablement.

### Completion and ambiguity

Cancellation retains authority until immediately before native dispatch.
After native activation may have begun, exceptions, malformed native output,
deadline, failed checkedness verification, navigation ambiguity, receipt
mismatch, dispatcher failure, or late cancellation become non-retryable
`browser_interaction_outcome_unknown`. The deadline atomically claims the
result and resolves the caller before UI-thread quarantine work, so a stalled
dispatcher or an earlier queued native result cannot convert a timeout into
success. Asura never redispatches check. Native-surface ambiguity attempts
adapter quarantine and replacement. Every unknown outcome becomes a
non-retryable failed tool result, skips the remainder of the stale batch, and
returns control to the provider for fresh inspection.

Success records `check_completed`. A wrong control returns
`browser_element_not_checkable`; a stale lease or non-interactable element uses
the existing closed errors. Neither failure makes an accepted reference
reusable.

`browser.check` is compiled and tested only through ADR 0026's explicit
`FullAutomationCandidate` profile. Production continues to advertise state
read, guarded navigation/history/reload, and stop only.

## Consequences

- Providers can request a checked final state without a toggle retry hazard.
- Already-checked controls are an event-free success.
- Checking one radio may uncheck another member of its native group.
- The operation grants no selector, arbitrary DOM property, script, keyboard,
  pointer, or secret authority.
- Availability is sacrificed after any uncertain activation so a model cannot
  duplicate or invert a possibly committed interaction.

## Limits and required evidence

- The fixed registry and captured methods execute in the page realm. Prototype
  and built-in poisoning before or after registry installation remains an
  enablement blocker; captured methods are not an isolated world.
- Synthetic `click()` activation is not trusted physical input. Framework,
  focus, user-activation, and event-order behavior can differ by native engine.
- A custom renderer must be fenced or detached after an ambiguous interaction,
  and approval must bind and display the trusted current origin, before the
  candidate profile can be enabled.
- WKWebView, WebView2, WPE WebKit, and any selected WebKitGTK fallback require
  named-platform hostile-page, checkbox/radio activation, event-order,
  navigation, cancellation, and quarantine evidence.
- Uncheck, custom ARIA controls, switches, select/listbox choice, and other
  stateful form operations remain separate slices.

## Alternatives rejected

- Reusing `browser.click` cannot prove a checked final state and can invert an
  already-checked checkbox.
- A general property setter would create an open-ended DOM mutation surface.
- Assigning `checked = true` and manually inventing events would define a
  second activation model instead of using native checkbox/radio behavior.
- A selector or locator replay can retarget after page mutation.
- Retrying an unknown result can reverse a successful checkbox mutation or
  repeat event-handler effects.
