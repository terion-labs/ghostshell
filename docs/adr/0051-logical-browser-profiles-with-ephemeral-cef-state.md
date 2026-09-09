# ADR 0051: Encrypted durable Chromium profile state

**Status:** Accepted
**Date:** 2026-08-28

## Context

Named browser profiles exist to preserve authenticated web sessions. A useful
durable profile therefore has to retain the complete Chromium request-context
tree, including cookies, local storage, IndexedDB, cache, service workers, and
navigation/session files. Definition metadata alone does not satisfy that
contract.

CEF requires a real directory for durable request-context state; it cannot use
an application blob store directly. Asura already has an OS-keystore-backed
application-encryption key and an encrypted LiteDB content-store pattern. A
mounted decrypted disk image would expose a broadly discoverable volume while
the app runs, so it is not used.

## Decision

`DurableMetadata` remains the serialized enum name for compatibility, but its
runtime meaning is a durable encrypted browser session. Definition revisions
are deliberately not part of state identity: renaming a profile or changing
its bounded HTTP-auth definition must not sign the user out. Profile,
partition, and network route are isolation boundaries. `PrivateSession` gives
each panel a separate context with no cache path and destroys it at the final
lease.

During a run, each durable request context receives an owner-only temporary
directory under CEF's private runtime root. CEF may use that directory normally.
After all browsers close, Asura releases request contexts, shuts CEF down
so Chromium has flushed its files, archives the complete context directory into
an encrypted LiteDB blob, atomically switches the manifest to the completed
blob, and removes the plaintext runtime tree. The archive rejects links,
absolute and escaping paths, duplicate targets, excessive entry counts, and
excessive expanded size.

CEF's runtime-global `Local State` is sealed separately in the same encrypted
store because Chromium needs its OS-crypt metadata to reopen cookies and other
protected context databases. On macOS the private runtime uses Chromium's mock
Safe Storage key, while Asura's application encryption protects the full
archive at rest. This avoids a second, Chromium-owned login-keychain prompt.
CEF initialization waits for startup unlock and both global and per-context
recovery.

Every runtime context directory has a bounded identity manifest outside its
CEF cache subtree. After an unclean exit, startup seals such orphaned trees
before profiles can open. If encrypted storage is expected but its key is
unavailable, recovery and durable profile acquisition fail closed without
deleting the orphan. Turning application encryption off is an explicit opt-out:
saved browser-session archives are deleted and live runtime trees are discarded
at shutdown rather than persisted in plaintext. Durable selections still open
as session-only contexts while retention is deliberately disabled.

Clear-data requests bind the exact profile and partition. Cookies and HTTP-auth
credentials can be cleared after inactive encrypted contexts are restored.
Whole-profile reset requires zero active leases and deletes both runtime and
encrypted state for every route of the selected partition. Cleanup never
touches downloaded user files. Browser permission requests remain denied and
visible. Downloads require a user-selected Save As destination outside the
profile and publish visible progress independently of profile retention.

Optional HTTP-auth metadata remains limited to exact host, optional port and
realm, Basic or Digest scheme, username, and a vault `SecretRef`. Portable
bundles strip credential references. OAuth remains an explicit user-initiated
“Open in system browser” action.

## Consequences

- Cookies, local storage, IndexedDB, cache, and the rest of Chromium's context
  state survive clean restarts for durable profiles.
- A private temporary directory exists while Asura is running because CEF
  requires filesystem storage. It is owner-only, is never presented as a
  mounted volume, and is removed after a successful encrypted seal.
- A crash can leave that private directory until next-start recovery; failure
  to recover is visible and fails closed.
- The macOS runtime uses `use-mock-keychain`; Asura's encrypted archive,
  rather than Chromium's separate login-keychain item, protects durable state
  at rest.
- The UI describes durable profiles as encrypted sessions restored between
  runs and private profiles as discarded when their panel closes.
- Cookie deletion is acknowledged by CEF and its cookie store is flushed before
  Settings reports success; HTTP-auth clearing and connection closure likewise
  wait for native completion callbacks.
