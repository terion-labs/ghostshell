# ADR 0020: Native webview wrapper and first browser capability slice

- Status: Accepted
- Date: 2026-07-23
- Superseded by: [ADR 0042](0042-cef-off-screen-browser-runtime.md)

## Context

The first executable browser panel must use each operating system's web engine without allowing Avalonia vendor types, native handles, JavaScript strings, or a Node.js browser process to leak into application and session-host contracts. It also needs to survive normal tab-template detach and reattach without silently reloading a page or duplicating history.

The complete browser design includes profiles, permissions, downloads, document snapshots, stable element references, interaction, waits, screenshots, and governed agent tools. Shipping those operations before every platform can expose a typed, enforceable contract would create a generic execution escape hatch.

## Decision

`Asura.Application` owns the engine-neutral browser values and ports. The initial closed capability set is:

- read URL/title/load state and document revision;
- navigate to an HTTP(S) URL or `about:blank`;
- back, forward, reload, and stop.

`Asura.Browser` is the only project that references `Avalonia.Controls.WebView` 12.0.1. Its public `BrowserSurface` implements the application renderer port; the package's `NativeWebView` remains private. The official wrapper selects `WKWebView` on macOS, the installed WebView2 runtime on Windows, and WPE WebKit on Linux with its GTK/WebKitGTK fallback.

The desktop composition root creates the native renderer view and supplies the logical `BrowserPanelSession` factory to the in-process session host. The host owns session identity, exact graph ownership, interactive attachment authority, cancellation, close, and typed dispatch. Presentation owns only the view lifetime and engine-neutral renderer reference.

Top-level navigation rejects every scheme except HTTP, HTTPS, and `about:blank`. New-window requests are handled closed, and developer tools are disabled. JavaScript evaluation, DOM/accessibility snapshots, profile mutation, cookies/storage, downloads, permissions, dialogs, certificates, screenshots, and agent automation are not advertised by this slice.

Detaching a renderer keeps the logical URL and monotonic document revision while making navigation unavailable. Reattaching the same retained renderer resumes its current state without navigation; attaching a different renderer explicitly opens the retained logical URL. Closing the owning panel closes the logical session. Renderers are never transferred between sessions.

Future browser automation must add closed request/result types, per-platform conformance evidence, document-revision-bound references, domain policy, untrusted-content labeling, and capability-broker authorization. It must not add a generic script or untyped execute operation.

## Consequences

- Desktop browser panels use host-native engines without bundling Node.js or an additional Chromium runtime.
- Application, protocol, and presentation projects remain independent of vendor webview APIs.
- The first shipped capability matrix is deliberately small and truthful.
- Browser profile, permission, download, crash-recovery, and automation work remains visible M3 scope.
- Linux packages require compatible system WPE WebKit libraries; Windows requires the WebView2 runtime.

## Alternatives rejected

- Launching a Node.js browser controller adds a second runtime and does not satisfy native-webview composition.
- Referencing native webview types from presentation couples UI state to one vendor API and prevents transport evolution.
- Bundling Chromium duplicates an engine the operating systems already provide and materially expands package and security ownership.
- Exposing raw JavaScript or a generic operation dictionary before typed governance makes capability enforcement unverifiable.
