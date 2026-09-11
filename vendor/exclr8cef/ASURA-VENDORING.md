# Asura Exclr8CEF source snapshot

This directory vendors Exclr8CEF commit
`7751a0b76cbabaf1fa81ef2b71b694a44c87f77e` and applies the reviewed
Asura hardening needed for a production off-screen browser host. The
resulting native binding version is `0.8.0-asura.11`.

`ASURA-PATCHSET.sha256` is the canonical, path-sorted manifest of every
file that differs from that upstream commit. Its own SHA-256 is recorded in
`licenses/cef-runtime-components.json` and in every generated CEF runtime
receipt. `ASURA-SOURCE-SNAPSHOT.sha256` separately binds every file in
this vendored tree outside generated dependency/build directories. Runtime
assembly rejects a changed, linked, missing, or unlisted source before
compiling, and receipts bind both manifest digests.

The local changes provide fail-closed main-frame navigation and resource
gates, opt-in/main-frame-only JavaScript bridge injection, normalized process
arguments, command-line switch suppression, macOS helper sandbox setup,
deterministic disposal, bounded CPU-OSR frame delivery, macOS Metal/IOSurface
accelerated presentation with a fixed-rate CEF frame clock and reusable
compositor-released buffers, and an
Avalonia-rendered browser context menu, browser-tab context commands, and
modifier/middle-click new-tab routing.

The v2 authentication callback carries CEF's challenge origin URL unchanged
to the managed boundary. The versioned export prevents older native libraries
from being mistaken for the origin-aware ABI used by saved credentials.

Hosted popups reserve a managed OSR browser before native creation and adopt
the original CEF popup instead of canceling/recreating its URL. This preserves
WindowProxy/opener, delayed blank navigation, postMessage and window.close
while keeping the popup inside an owning host surface and request context.
Creation abort and opener teardown release pending reservations. A host that
does not accept ownership cannot create an untracked native popup window.

Disk-backed request contexts explicitly persist session cookies across process
restarts. Contexts without a cache path remain in-memory and do not retain them.
Request-context paths are canonicalized before CEF compares them with its root
cache path, including the macOS `/var` to `/private/var` alias.

Windows CEF 150 sandboxing cannot be implemented inside this managed-host
shim: CEF requires its native bootstrap executable and client DLL to own the
process entry point before the CLR starts. Windows production artifacts remain
blocked until that launcher exists. An explicit sandbox-off build is permitted
only for local development.

The macOS host disables interactive file-Keychain access before loading CEF
in every browser/helper entry point. Asura packaging scopes the pinned CEF
framework service literal with `scripts/scope-cef-keychain.py` before signing;
it preserves real random-key cookie encryption and separates development keys.
