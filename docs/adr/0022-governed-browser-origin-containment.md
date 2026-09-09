# ADR 0022: Governed browser origin containment

- Status: Superseded for product origin containment; retained for navigation serialization
- Date: 2026-07-24
- Extends:
  [ADR 0020](0020-native-webview-wrapper-and-first-browser-capability-slice.md),
  [ADR 0021](0021-governed-browser-state-and-navigation.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

ADR 0021 authorized one exact requested browser action, but its first slice did
not constrain redirects after native dispatch. An approved same-origin URL
could therefore redirect to a different scheme, host, or port before the host
recorded the action as successful. Browser snapshots and page interaction would
make that gap more dangerous because later tools could act on the escaped
document.

The native Avalonia webview boundary exposes a cancellable top-level
`NavigationStarted` event for initial requests and redirects. It does not expose
a portable cross-platform navigation identifier, so Asura also needs
serialization, a wrapper-owned monotonic generation, loading-state checks,
final-address validation, cancellation, and late-event suppression at its own
renderer boundary.

## Decision

### 2026-08-25 peer-binding amendment

The shipped CEF adapter cannot attest or bind policy to the actual connected
peer. Asura therefore does not rely on this historical top-level origin
guard as SSRF containment. Every model-governed CEF operation capable of
causing network activity now fails before native dispatch, including
navigation/history/reload, element interaction, low-level automation, and the
detached rendered web-read/search paths. No governed redirect, frame,
subresource, service-worker, or download request is created. Human browsing
uses its separate authenticated chrome path, and bounded observations may read
content already loaded by the human.

Re-enabling governed CEF dispatch requires a first-party request-scoped proxy
or equivalent transport boundary that preserves hostname TLS validation while
checking every actual peer, redirect, and subresource. SSH routing additionally
requires a separately named exact capability and enforcement at the remote
resolution and connection boundary.

### 2026-08-16 amendment

Asura no longer treats the current site origin as a browsing allowlist.
After ordinary capability authorization, SessionHost supplies an unrestricted
navigation boundary, so explicit navigation, redirects, history movement, and
links activated by governed interaction may cross origins. `about:blank` may
bootstrap any address accepted by `BrowserAddress`.

The renderer guard remains as a typed execution boundary because it also owns
starting-document/revision validation, one-operation serialization, terminal
completion, cancellation, late-event fencing, and unknown-outcome handling.
Those invariants remain in force. The exact-origin mode is retained for
lower-level conformance tests and embedders but is not selected by the product
SessionHost. Authorization-source failures use
`browser_action_not_authorized`; they are not presented as domain policy.

The original decision follows as historical context.

Asura adds an application-owned origin-containment capability,
`browser.navigation_origin_guard`. Governed `navigate`, `back`, `forward`, and
`reload` tools are advertised, composed, and dispatched only when the exact
browser session exposes this capability. `read_state` and `stop` do not require
it.

The application contract contains:

- a canonical origin value using lower-cased scheme, IDN-normalized host, and
  effective port;
- an explicit `about:blank` origin that allows only `about:blank`;
- a closed renderer request union for navigate, back, forward, and reload;
- a starting-document binding containing the trusted committed address and
  document revision; and
- a narrow renderer/session operation that executes one request inside one
  frozen origin.

After consuming the one-action authorization, and again immediately before
dispatch, SessionHost derives the frozen origin from trusted state:

- explicit navigate uses the approved destination origin;
- reload uses the current committed origin;
- back and forward use the current committed origin, conservatively rejecting
  a first cross-origin history destination;
- `read_state` and `stop` do not install a guard.

The same decision also freezes the current committed address and document
revision. The renderer rechecks that starting-document binding on its UI thread
immediately before native dispatch. A changed document fails retryably with
`browser_state_changed`; no native navigation is issued.
`BrowserPanelSession` first validates this binding in its monotonic logical
revision space, then atomically translates it to the exact last-projected
renderer-local address and revision. A renderer replacement therefore cannot
make a valid logical document look stale, while an unprojected renderer change
still fails closed.

The domain decisions from ADR 0021 still apply first. `YoloPolicy`, and
`AutoPolicy` history traversal or cross-origin explicit navigation, fail with
`browser_domain_policy_denied` even if a page is already loading. An otherwise
authorized guarded mutation fails with retryable `navigation_in_progress` when
the browser already has an unresolved load, because an older native event
cannot be safely correlated to a new attempt.

The native wrapper installs the guard before dispatch. Every top-level
`NavigationStarted` event during that governed attempt is checked
synchronously. A request outside the frozen origin is cancelled before the
renderer accepts it. Same-origin redirect hops remain allowed. The final
completed address is checked again as defense in depth.

The wrapper assigns one increasing local generation to the explicit native
dispatch and reuses it for redirect starts until the first terminal completion.
The surface remembers terminal generations and ignores duplicate or stale
starts, rejections, and completions while that adapter remains authoritative.

Governed navigation stays pending until the native engine reports final
completion, rejection, or cancellation. Only then may SessionHost reconcile
state and completion-audit `navigate_completed`, `back_completed`,
`forward_completed`, or `reload_completed`. A redirect denial returns
`browser_domain_policy_denied`, keeps the last committed address and document
revision, and never redispatches during audit recovery.

Cancellation, policy rejection, and an explicit stop perform a best-effort
native stop where applicable and preserve the committed state. Because the
portable vendor contract has no navigation identifier, the surface retains a
draining guard after the governed result completes. While draining, every
delayed top-level start is cancelled, unrelated terminal events are ignored,
and new human or governed navigation returns retryable
`navigation_in_progress`. Only the terminal completion carrying that locally
assigned generation can finish the drain. Asura then removes every event
subscription from the quarantined adapter and replaces the entire native
webview before permitting another navigation. Any later vendor callback remains
confined to the old adapter; every handler also rechecks sender identity in case
an invocation list was captured before unsubscription, so it cannot be
mislabeled as a newer navigation.
The fresh adapter starts at `about:blank`, clears native history, and advances
the document revision. If replacement fails, navigation remains unavailable
rather than reusing an ambiguous adapter. Unsupported-scheme redirect rejection
uses the same `browser_domain_policy_denied` path.

`BrowserPanelSession.StopAsync` may bypass the operation queue only to
interrupt an active governed navigation. Attach, detach, close, ordinary
navigation, and their stop calls remain serialized. The session retains the
governed queue until any concurrent stop interruption has returned. It also
cancels a session-owned linked authority token before invoking native Stop, so
a stop that wins before renderer/UI-thread registration prevents the governed
request from dispatching afterward.

No Node.js process, CDP controller, raw JavaScript, or vendor webview object is
introduced.

## Consequences

The following consequences describe the retained origin-guard machinery and
its conformance fixtures. They are not a claim that the shipped CEF adapter
dispatches model-governed network operations.

- Every synchronously observed and cancellable top-level start in a governed
  request and redirect chain remains inside one exact origin.
- The broker records success only after final native completion, rather than
  after merely queuing a navigation.
- Human-approved history navigation is deliberately more conservative than
  ordinary browser chrome when the history destination crosses origins.
- Browser Stop remains responsive while a governed navigation awaits the
  engine.
- A cancelled or rejected native attempt temporarily makes further navigation
  unavailable until its terminal event drains and a fresh native adapter is
  installed. The safety reset returns the panel to `about:blank`, advances its
  document revision, and clears native history.
- Renderers without synchronous top-level interception receive no governed
  navigation tools.

## Limits and required evidence

- The historical origin guard applies to top-level navigation, not
  subresources, frames, service workers, downloads, or new-window creation.
  This is why it is insufficient to re-enable governed CEF dispatch. New
  windows remain blocked by ADR 0020.
- A same-origin page-initiated navigation is indistinguishable from a
  same-origin redirect on engines without a portable navigation identifier.
  The local generation deliberately treats such interleaved starts as one
  active chain. Serialization, starting-document binding, and terminal
  draining bound this ambiguity, but do not manufacture a vendor identifier.
- If a native engine accepts a request but never reports its terminal event, or
  a fresh adapter cannot be created, Asura deliberately keeps that
  renderer's navigation path unavailable rather than guessing that a late
  event is safe. Named-platform conformance must prove terminal-event behavior,
  adapter teardown, replacement, and recovery.
- The final-address check fails the governed action if an adapter omits a
  redirect-start event, but it cannot retroactively prevent a document that an
  incorrect adapter already committed.
- WKWebView, WebView2, WPE WebKit, and any selected WebKitGTK fallback still
  require named-platform conformance evidence for cancellation ordering,
  redirect coverage, and late-completion behavior before the cross-platform
  browser-automation exit criterion is satisfied.

## Alternatives rejected

- Checking only the requested URL leaves redirect-based origin escape open.
- Allowing back or forward to capture an unknown first origin cannot reliably
  distinguish the approved history action from a racing page-initiated
  navigation without a portable navigation identifier.
- Returning success immediately after native dispatch records an effect before
  its origin and completion are known.
- Launching a Chromium/CDP sidecar would bypass the selected native-webview
  boundary and add the separate Node.js runtime the product does not need.
