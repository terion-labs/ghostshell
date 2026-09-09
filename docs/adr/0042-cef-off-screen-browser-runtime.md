# ADR 0042: CEF off-screen browser runtime

- Status: Accepted
- Date: 2026-08-08
- Supersedes: browser-engine and native-child-view decisions in
  [ADR 0004](0004-native-browser-adapters.md) and
  [ADR 0020](0020-native-webview-wrapper-and-first-browser-capability-slice.md)

## Context

The operating-system webview implementation made browser panels native child
views. That prevented reliable Avalonia z-order, clipping, transforms, and
overlays, and required separate WKWebView, WebView2, and WebKit behavior. The
browser is a first-class panel, so those composition differences are product
constraints rather than an acceptable implementation detail.

CEF supplies one Chromium engine and an off-screen rendering (OSR) contract on
macOS, Windows, and Linux. It also makes Asura responsible for a large
multiprocess runtime, Chromium security updates, sandboxing, helper processes,
licenses, signing, and orderly process shutdown.

The published Exclr8CEF 0.8 packages cannot supply that runtime: their managed
package graph references unavailable per-RID packages. The reviewed source
revision also needs Asura-specific lifecycle, request-policy, and security
fixes.

## Decision

Asura vendors Exclr8CEF commit
`7751a0b76cbabaf1fa81ef2b71b694a44c87f77e`, applies a hashed local patch set,
and pins CEF `150.0.9+g81b0088+chromium-150.0.7871.46`.

`Asura.Browser` is the only product project that references the binding.
Its public contracts remain engine-neutral. `CefBrowserView` hosts the binding's
OSR control as an ordinary Avalonia visual, while `BrowserSurface`
owns logical state, origin containment, crash replacement, and deterministic
disposal. Layout rebuilds reparent the same visual and preserve the panel-owned
session attachment; they do not suspend, conceal, or recreate the browser.

CEF subprocess dispatch happens before single-instance, storage, or Avalonia
startup. Process initialization happens after Avalonia setup, uses an exact
runtime-version check, private request contexts, no remote debugging
port and an opt-in-disabled JavaScript bridge. Popups cross an explicit shell
new-tab policy. JavaScript/file dialogs, permission requests, certificate
exceptions, downloads, find results, and renderer failure cross a closed
Asura-owned product-event family; vendor event types remain private.
Dialogs, permissions, and certificate exceptions still default closed.
Downloads use an explicit Save As destination and publish typed progress, while
find-in-page delegates through a narrow engine-neutral controller.
Durable named profiles use private runtime directories whose complete state is
sealed into encrypted application storage after CEF flushes; private sessions
receive no cache path. The lifecycle is specified by
[ADR 0051](0051-logical-browser-profiles-with-ephemeral-cef-state.md).
Top-level navigation is admitted by a cancellable main-frame callback; the
resource-request gate separately prevents unapproved local-file subresources.
Callback and dispatcher failures deny requests.

Panel close removes the control from the visual tree before disposal. Shutdown
force-closes all remaining browsers, continues the external CEF pump until every
`OnBeforeClose` is observed, and only then calls `CefShutdown`.

macOS uses CEF accelerated paint and copies each borrowed IOSurface into an
application-owned IOSurface with Metal before the callback returns. Avalonia
imports that surface through timeline-semaphore GPU interop, so browser pixels
remain in GPU memory. Accelerated browsers opt into CEF external begin frames;
the native shim requests one Chromium frame on each CoreVideo CVDisplayLink
callback, so Chromium follows the hardware display cadence instead of a fixed
60 fps timer. The clock stops while detached and does not invalidate or redraw
sibling visuals. Pending frames are coalesced so neither accelerated nor
CPU fallback rendering can build an unbounded queue. Linux DMA-BUF and Windows
shared-handle presentation remain unqualified and use the CPU fallback.

Setting `EXCLR8CEF_ACCELERATION_DIAGNOSTICS=1` records CoreVideo callback
cadence and jitter, CEF paint and presentation cadence, GPU-copy/import
latency, coalesced frames, Chromium task metrics, and HTML video decoded and
dropped-frame counters. For controlled comparisons,
`EXCLR8CEF_FRAME_PACING=display-link` enables the experimental CoreVideo clock;
the production default keeps SharedTexture/IOSurface presentation while using
CEF's 60 fps windowless timer.

CEF 150's external-begin-frame API accepts neither the display timestamp nor
the refresh interval and internally labels external frames with its 60 Hz
default. On macOS the CoreVideo clock therefore selects display callbacks
nearest to 60 Hz deadlines, coalesces pending work, and invokes CEF on
`TID_UI`. This preserves display phase without flooding CEF with 120 Hz
ProMotion callbacks or mutating compositor state from CoreVideo's thread.
The mode remains experimental because an A/B trace on a 120 Hz display showed
that CEF's timer decoded and presented a 30 fps YouTube stream with zero drops,
while external begin frames caused CEF to drop roughly 12 percent before
`OnAcceleratedPaint`. SharedTexture/IOSurface acceleration is unchanged in the
default timer mode.

The semantic snapshot/click/fill/check implementation is deliberately excluded
from this migration. Existing application contracts remain, but the production
CEF adapter fails those operations closed until the separate agentic-browser
design pass.

Native artifacts are built per RID into a private staging tree, verified against
the reviewed CEF catalog, and published with a receipt that binds the upstream
archive hashes, binding commit, local patch digest, and every staged file.
macOS packages contain the framework and five correctly named helper bundles in
`Contents/Frameworks`; Windows and Linux use a flat runtime closure beside the
app host.

## Security and release gates

- macOS and Linux builds default to the CEF sandbox and cannot silently opt out.
- CEF 150 Windows sandboxing requires a native bootstrap/CLR launcher. The
  current .NET-hosted shim rejects secure Windows configuration rather than
  claiming that a null sandbox pointer is safe. An explicitly unsandboxed build
  is development-only; Windows release remains blocked on that launcher.
- Linux release qualification must verify the installed `chrome-sandbox`
  ownership/mode or an accepted user-namespace configuration.
- Chromium updates follow an owned security SLA and rebuild/acceptance matrix
  for every supported RID.

## Consequences

- Browser panels participate in normal Avalonia composition and overlays.
- macOS, Windows, and Linux share Chromium behavior and one adapter boundary.
- Distribution size, memory use, packaging complexity, and security ownership
  increase materially.
- macOS browser frames remain GPU-resident after CEF's required owned copy and
  use display-linked external begin frames. Other platforms still pay the CPU OSR
  copy/upload cost until their shared-texture contracts are qualified.
- Windows production packaging is fail-closed until its sandbox bootstrap is
  implemented and qualified.

## Alternatives rejected

- Keeping host-native webviews preserves the composition defects that motivated
  the migration and retains three behavior matrices.
- Consuming the published Exclr8CEF NuGet packages cannot produce a complete
  native runtime and omits required fixes.
- Pretending one shared-texture ABI is portable across IOSurface, NT-handle, and
  DMA-BUF platforms would make the fastest path the least reliable one.
- Reintroducing raw JavaScript automation during the renderer migration would
  mix the explicitly deferred agentic trust model into the engine boundary.
