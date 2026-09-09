# ADR 0052: Source-aware Velopack updates

**Status:** Accepted
**Date:** 2026-08-30

## Context

Asura currently publishes a signed and notarized macOS archive through
GitHub Releases. Future builds may come from the Apple App Store, Microsoft
Store, or a Linux package manager. An app-store build must not replace itself
with a GitHub package, and a direct build should not send users to a browser for
an update it can safely download itself.

The update decision must come from the installed artifact. Runtime platform
detection cannot distinguish a GitHub build from an App Store build on the same
machine.

## Decision

Every packaged application carries a strict `distribution.json` manifest. It
records the distribution source, update strategy, package identifier, channel,
and runtime identifier. The app rejects unknown fields, mismatched package or
runtime identifiers, and invalid source/strategy pairs. The direct feed host and
repository remain compiled into the provider, so local manifest tampering cannot
redirect downloads.

Direct GitHub builds use Velopack 1.2.0. The channel combines runtime and track,
for example `osx-arm64-stable`, so a feed cannot cross operating systems or CPU
architectures. Update checks run only after the user selects "Check for
updates". A second action downloads the selected package. "Restart to update"
arms Velopack's external updater and then requests Asura's normal shutdown,
which preserves the existing session, recovery, database, and browser cleanup.
Automatic startup checks and automatic startup application are disabled.

Store and package-manager builds use `platform-managed`. They display their
install source and expose no Velopack actions. Development builds without a
trusted manifest also expose no update actions.

`VelopackApp.Run()` is the first operation in the desktop entry point, before
private helpers, CEF subprocess dispatch, or Avalonia setup. This ordering is a
Velopack process contract, not an update check.

## Release assembly

Existing ZIP installations do not contain Velopack's `UpdateMac` and
`sq.version`, so `UpdateManager.IsInstalled` is false and the About page explains
that the bundle cannot update in place. A user installs one update-aware release
through the ZIP before in-app updates become available.

`package-macos-github-release.sh` first assembles and signs every nested Native
AOT and CEF binary. Pinned `vpk` then copies that app, adds `UpdateMac` and the
fixed `sq.version` resource/link, signs only its updater and the final outer app,
and notarizes and staples the result. The portable ZIP, full package, and channel
feed are derived from that final app. A repository validator hashes every app
file in the full package against the extracted portable app, checks the feed's
exact package size and SHA-256, and accepts only Velopack's one fixed in-bundle
metadata link. Exact release evidence is assembled only after these checks.

The direct lane deliberately passes `--noInst`: the existing ZIP is the
bootstrap artifact, and an unsigned `.pkg` is not published. Adding a package
installer requires a provisioned Developer ID Installer identity and a separate
signed/notarized installer evidence boundary.

## Consequences

The UI and application layer do not depend on Velopack. Adding a store channel
requires a new manifest source/strategy pair and composition, not conditionals
inside the direct updater. GitHub checks remain user initiated. Downloads use
Velopack's package checksum and cache, and installation waits for a graceful app
exit.

Velopack can request elevation for a bundle in `/Applications`. Asura does
not offer download or apply actions for those system-wide installs, avoiding a
privileged local-package replacement path; they require the signed installer.
App Store builds remain sandboxed and platform managed; Velopack does not support
the macOS App Sandbox.
