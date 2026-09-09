# ADR 0003: SQLite persistence, migrations, and recovery

- Status: Accepted
- Date: 2026-07-22

## Context

M1 requires durable definitions, runtime recovery metadata, audit records, and migrations. Secrets must never be stored in the application database.

## Decision

Use SQLite through `Microsoft.Data.Sqlite` behind repositories in `Asura.Infrastructure`. Enable WAL, foreign keys, a bounded busy timeout, and explicit transactions. Use normalized columns for identity, ordering, lookup, lifecycle, and audit correlation. JSON columns are accepted only behind explicit, versioned codecs; audit callers provide a closed `AuditDetails` value instead of arbitrary JSON.

Maintain a monotonic schema-version table and transactional forward migrations. Before a destructive migration, create and validate a recoverable backup. Startup writes a dirty-shutdown marker before opening runtime state. The previous marker is parsed and validated inside the startup transaction before it can be replaced; malformed identifiers, state combinations, or timestamps fail closed without changing the stored row. An unclean marker opens recovery choices without deleting durable definitions.

The marker becomes clean only after an explicit shutdown barrier: presentation producers are quiesced and their graph watches have stopped, including watches blocked on UI dispatch; the recent-session queue is sealed and drained; host-owned sessions are disposed; and the runtime-recovery writer atomically rejects new work and drains every write accepted before the seal. The first accepted history or recovery write failure remains authoritative even if later writes succeed. Any failure leaves the marker dirty. Dependency-injection disposal may run afterward, but no component can publish additional recovery state once the barrier succeeds.

Runtime recovery is bounded independently of portable definitions. Identifiers
are limited to 256 characters, each snapshot payload to 2 MiB, each run to 32
snapshot keys, and one restore to 16 MiB. The writer keeps at most 32 distinct
pending keys and coalesces queued whole-state snapshots for the same key to
their newest value. A superseded same-key value is drained by persisting that
newer value; an in-flight failure remains sticky even when a later coalesced
value succeeds. The previous-run inventory selects at most the newest 100 runs
through the recovery timestamp index, reads no payload bodies, and discloses
when older runs remain.

Every recovery deletion opens an immediate transaction, validates one dirty
lifecycle row, and protects its active run. In the desktop composition, that
row must exactly match the process run already recorded in
`ApplicationStartupState`; a syntactically valid but different run fails
closed. Once a delete statement completes, commit and acknowledgement are not
cancelled, so a committed removal is never reported as cancelled. The
presentation boundary receives only grouped counts, byte sizes, and UTC
timestamps; snapshot payloads and opaque run identifiers are not displayed.

Desktop profile ownership is established before dependency injection and SQLite initialization. A primary process owns a per-profile application lock and a current-user-only activation endpoint whose random name is published beside that lock, so aliases of the same profile share ownership without exposing a stable global endpoint. A secondary launch requests activation of the existing main window and exits without touching the database only after the primary has completed lifecycle/catalog initialization and its UI handler accepts the request. Activation is stopped before the primary releases its profile lock during shutdown. Endpoint failure is bounded and produces a sanitized, keyboard-accessible startup window as well as stderr output, so a packaged GUI launch never fails invisibly. The SQLite profile lock remains a second defensive boundary rather than the normal single-instance mechanism.

Released migration identity is immutable: version, name, SQL checksum, and destructive classification are frozen in historical fixture receipts. Compatibility tests construct every supported historical schema, load its durable definitions through the production catalog, preserve the released desktop recovery key/schema, and prove rollback plus retry after an induced migration failure.

A destructive-migration backup is not discoverable as trusted recovery data until it is complete. The database is copied to a unique same-directory temporary path, checked for SQLite integrity and foreign-key violations, closed, and atomically moved to its final collision-safe name. Cancellation and validation failure remove temporary database artifacts; cleanup uncertainty is surfaced instead of leaving an unvalidated file that resembles a usable backup.

Current definition snapshots are the fast source of truth. Agent/action audit and selected runtime events are append-only, but the application does not use full event sourcing. Terminal scrollback is stored as bounded segments and indices only when enabled.

SQLite stores non-sensitive metadata about secret access, never the raw `SecretRef` string. Vault-access audit events use fixed action/outcome mappings, allowlisted purpose/error enums, and a process-keyed reference pseudonym; user-controlled purpose target IDs and secret material are structurally omitted. The pseudonym links requested/completed events without treating an identifier-shaped credential as safe to log. It is intentionally not stable across process restarts. An OS vault adapter is the only persistent secret store; no available vault means fail closed or explicitly memory-only credentials.

Saved-screen startup-command events use the same closed-codec rule. Their details contain only a bounded command count and an allowlisted dispatch error code; command text and terminal output are never persisted in audit JSON. A failed pre-dispatch audit write prevents terminal input. If delivery succeeds but the completion audit fails, the result records that commands may not be retried and the UI exposes the reconciliation failure.

A failed requested-audit write prevents the vault operation. Because an OS-vault mutation and SQLite cannot share a transaction, a failed completion-audit write returns the distinct `AuditPersistenceFailure` result; callers must reconcile vault state before retrying.

Recent-session history uses a normalized, closed-column record: runtime session ID, stable source-definition kind and ID, panel kind, a snapshot of the durable definition's display name, start/end timestamps, and an allowlisted lifecycle outcome. It has no JSON/detail payload and never stores terminal titles, command text, terminal content, environment values, credentials, or secrets. Lifecycle timestamps are captured when the application observes the start or completion, before serialized persistence work can be delayed. Both persistence and queries have hard count bounds; every production transaction reads the current local retention policy; age retention is enforced during reads and writes; and a zero-record policy deletes and disables history. The policy is a revisioned local singleton rather than a portable definition. Updates require the expected revision and persist the new policy plus its immediate prune in one transaction.

Selective clearing uses a caller-captured confirmation cutoff so sessions completed after confirmation are not removed. A separately confirmed recovery reset invokes unconditional clearing to remove even malformed rows that the safe reader refuses to expose; late completion events never recreate cleared records. History export is a bounded, versioned, deterministic JSON document with the closed content marker `definition-metadata-only` and an explicit allowlist of the normalized columns above. The app publishes it through a same-directory temporary file and atomic move, preserves an existing destination on export failure, and surfaces cleanup uncertainty rather than claiming that residual metadata was removed.

## Consequences

- Repositories isolate schema details from Core and Application.
- WAL improves desktop read/write concurrency but requires correct checkpoint and backup handling.
- A second desktop launch raises the existing instance instead of competing for lifecycle ownership.
- Recovery cleanliness is a producer-shutdown invariant, not merely a process-exit callback.
- Migration fixtures cover every supported schema version and unclean shutdown, and adding a schema requires adding a frozen receipt plus a failure-injection fixture for its predecessor.
- Export/import can omit secrets by construction.

## Alternatives rejected

- Ad hoc JSON files make atomic multi-definition updates, migrations, and audit queries fragile.
- Full event sourcing adds recovery and evolution cost without a product requirement.
- Reversible application-managed encryption is not an acceptable substitute for OS credential stores.
