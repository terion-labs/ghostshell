# ADR 0026: Native browser capability conformance gate

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0020](0020-native-webview-wrapper-and-first-browser-capability-slice.md),
  [ADR 0021](0021-governed-browser-state-and-navigation.md),
  [ADR 0023](0023-governed-native-document-snapshots.md),
  [ADR 0024](0024-governed-browser-element-click.md),
  [ADR 0025](0025-governed-browser-element-fill.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

Asura has closed native C# contracts and fixed adapter scripts for browser
snapshot, exact-reference click, and bounded fill. Those scripts execute in the
page JavaScript realm, not an isolated browser world or native accessibility
API. A hostile page can poison realm-visible built-ins and prototypes before or
after registry installation or snapshot capture. Changing `Map`, `Set`,
`Function.prototype.call`, or related APIs can undermine assumptions made by
exact-object storage, type allowlists, and captured-method invocation.

Portable unit tests prove parser, lifecycle, approval, capability, and
fail-closed behavior. They do not prove the integrity or event ordering of
WKWebView, WebView2, WPE WebKit, or a selected WebKitGTK adapter on a named
platform. Advertising element automation in production before that evidence
would turn implemented code into an unsupported security claim.

## Decision

Browser capabilities use two shared immutable profiles:

- `Production`: `browser.state.read`, `browser.navigate`, `browser.back`,
  `browser.forward`, `browser.reload`, `browser.stop`, and
  `browser.navigation_origin_guard`;
- `FullAutomationCandidate`: the production profile plus `browser.snapshot`,
  `browser.click`, `browser.fill`, and `browser.check`.

Production is the default. The full-automation candidate must be injected
explicitly and is used by behavior tests and future conformance work. Its name
does not claim conformance. A production composition may select it only after
the named native adapter supplies the required hostile-page, registry,
mutation, synthetic-event, navigation-order, cancellation, and quarantine
evidence.

One profile is fixed for a browser session factory, every session it creates,
and each renderer surface. Desktop creates the renderer from the exact profile
owned by the concrete factory registered into SessionHost. Renderer attachment
requires exact set equality: missing and extra capabilities both fail.
SessionHost also snapshots the factory profile used for negotiation and
disposes any newly created session whose capabilities differ before
registration. Host, session, and renderer therefore cannot independently drift
into a wider contract.

Production preserves governed navigate, back, forward, reload, state read, and
stop. Snapshot, click, fill, and check are absent from provider tool schemas because
their individual capabilities are absent. The full typed operations remain
compiled and continuously tested through an explicit candidate profile without
becoming production claims.

SessionHost now projects an immutable browser-document identity into every live
browser session descriptor and agent context: the canonical current
`BrowserNavigationOrigin` plus the monotonic document revision. The identity is
included in the context fingerprint and is refreshed from the trusted browser
session state whenever the host resolves a context. Click, fill, and check
preparation require that projected revision to equal the requested element
revision. Their approval presentation and raw material digest also bind the
canonical origin. Execution reconstructs that material from a freshly resolved
context, so missing metadata, revision drift, or origin drift fails before
authorization consumption or native dispatch. The origin comes only from the
trusted committed browser address; page-authored title, role, and name never
provide authority.

Before candidate enablement, an ambiguous click/fill/check from any custom
`IBrowserRenderer` must still cause the session to fence or detach that
renderer, rather than relying only on `BrowserSurface` adapter replacement.
Displaying the snapshotted untrusted role/name remains desirable review
context, but must never become trusted authority.

## Consequences

- Human browser chrome and governed navigation retain their current production
  behavior.
- Snapshot, opaque element references, click, fill, and check are not advertised by
  the production desktop while page-realm integrity remains unproven.
- Candidate interaction approvals identify the exact trusted source origin and
  reject stale document context before authorization can be consumed.
- Adding a named-platform conformance receipt becomes a deliberate composition
  change rather than an incidental capability-list edit.
- Capability loss cannot be hidden by attaching a wider renderer, and a
  renderer cannot add ambient authority beyond its session.

## Alternatives rejected

- Advertising every implemented method would overstate the native adapter's
  security evidence.
- Removing the full implementation would discard useful contract, parser, and
  lifecycle tests while doing nothing to improve the production boundary.
- Trusting captured page-realm functions as an isolated-world substitute would
  ignore both pre-installation and post-capture prototype poisoning.
- Maintaining independent capability lists in SessionHost and Desktop would
  permit accidental drift.
