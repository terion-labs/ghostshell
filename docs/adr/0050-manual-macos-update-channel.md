# ADR 0050: Manual macOS update channel

**Status:** Superseded by [ADR 0052](0052-source-aware-velopack-updates.md)
**Date:** 2026-08-26

## Context

Asura publishes Developer ID-signed and notarized macOS arm64 archives to
GitHub Releases. The desktop had no updater, but its About page described
updates as unconfigured. That left background network behavior, package
authentication, deferral, installation failure, and rollback undefined.

An automatic feed or in-app installer would add a persistent remote trust
boundary. It would need authenticated metadata, replay and rollback controls,
atomic installation, recovery, notification preferences, accessibility, and a
credential-free signature-verification design. The current product does not
need that machinery.

## Decision

The supported macOS arm64 package uses a manual GitHub Releases channel.
Asura does not contact an update endpoint, fetch or display remote release
metadata, download packages, or modify its application bundle. The About page
reports `Manual · GitHub Releases` and `Not checked · automatic updates are
off`. There is no opt-out setting because network checks and notifications are
always off; a user defers indefinitely by doing nothing.

The release page supplies the archive and SHA-256 sidecar over HTTPS. The tag
workflow binds the archive to an exact source commit and tree, applies the
Developer ID signature, obtains and staples notarization, extracts the archive,
and verifies it with `codesign` and Gatekeeper before publication. The app
contains no signing certificate, private key, notary credential, GitHub token,
or update service credential.

Installation remains an explicit user operation. A download, checksum,
signature, notarization, or Gatekeeper failure occurs before the installed
bundle changes. User data lives outside the bundle. Users keep the prior bundle
until the new version launches successfully. Downgrade after a durable-schema
migration is allowed only when release notes declare compatibility or the user
restores a pre-update backup/export.

## Platform scope

macOS arm64 is the only supported release package. Windows, Linux, Intel macOS,
platform-store distribution, signed feeds, delta updates, and unattended
installation remain unavailable. A future platform package must document and
test its own manual or automatic update path before the About page advertises
it.

## Consequences

Asura has no background update traffic and cannot silently replace a
working installation. Users must discover, download, and install releases
themselves. Remote freshness is intentionally unknown inside the app. A future
automatic channel requires a new decision and security boundary rather than a
network call added to the About view model.
