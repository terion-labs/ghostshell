# ADR 0047: Governed local Git tools

## Status

Accepted — 2026-08-26.

## Context

Asura already has a user-operated Git panel, but model-originating Git
work needs a narrower boundary. Exposing repository paths, command arguments,
raw refs, remote URLs, or the ordinary UI client directly would let provider
text choose authority-bearing native operands. Git mutations can also race a
human action, execute hooks, publish the wrong revision, or become ambiguous
after cancellation or connection loss.

Windows and Linux desktop porting is deferred. SSH Git sessions have not yet
proved equivalent noninteractive commit and mutation containment. Neither is
accepted by this decision.

## Decision

Expose eight closed model tools for a live, local macOS Git panel:
`git.read_state`, `git.read_diff`, `git.read_remote_ref`, `git.stage`,
`git.unstage`, `git.branch_create`, `git.branch_checkout`, `git.commit`, and
not `git.push`. Repository observations require the append-only `GitData`
capability, which defaults to Off. Mutations require `Git`, which defaults to
Ask. The closed push contract remains reserved, but the session does not grant
its capability until an authenticated HTTPS smart-Git transport has live
containment evidence.

The model receives bounded, secret-screened projections and opaque expiring
references. It never supplies a repository path, URL, raw object ID, full ref,
refspec, executable, arbitrary argument, configuration, hook, environment, or
credential. A hosted Git session binds each reference to one canonical
repository identity and exact HEAD, index, worktree, local-ref, and remote-name
guard. State truncation removes mutation-capable references.

The UI and agent use one repository-keyed mutation coordinator. A typed action
composer binds the exact panel, hosted-session metadata, policy generation, and
opaque request material before approval. SessionHost consumes one permit,
re-resolves the exact panel and session, rejects drift, and dispatches through
the structured Git client once. A mutation authorization derived from ordinary
Auto policy is rejected at the host even if presented by a faulty caller.

Governed commits use fixed arguments with hooks, editors, and signing disabled.
They capture the exact staged index tree immediately before dispatch, then
verify that the resulting commit has the expected HEAD as its sole parent and
that exact tree. Stage and unstage bind the selected opaque change, prove the
selected path's exact post-index state, and prove every unrelated index entry
was unchanged. Cancellation or failure after mutation may
have started is non-retryable and quarantines later writes until explicit state
reconciliation. The original action is never replayed.

Governed commands clear inherited Git executable and configuration environment,
pin hooks, file-system monitors, credential helpers, external protocols, and
signing to inert values, and reject reachable repository configuration that can
execute filters, external diff drivers, or URL rewrites. Remote reads resolve
the configured destination before authority
and again before dispatch, reject repository-local HTTP, credential, proxy, and
client-identity configuration, clear inherited proxy and TLS-identity
environment, and execute outside the worktree so `.git/config` cannot be
rediscovered. Only credential-free HTTPS URLs are accepted. Local paths and
`file://` URLs are rejected because a future push to a local bare repository
could execute server-side receive hooks in the desktop process. SSH, remote
helpers, custom protocols, URL fragments, user information, and relative paths
are also rejected. Redirect following is disabled so the observed branch can
only come from the exact approved HTTPS origin and path.

Tool advertisement fails closed. The desktop factory and runtime advertise
this family only for a live local macOS hosted session. Windows, Linux, unknown
platforms, and SSH Git sessions advertise none; they are `notApplicable` under
the current porting-deferred release scope, not tested or passed platforms.

## Consequences

- Ordinary user-operated Git features remain available and keep their existing
  semantics; only model actions use this governed boundary.
- Repository open/discovery, trust changes, discard/reset/clean, branch delete
  or rename, merge/rebase, fetch/pull, tags, stashes, worktrees, submodules,
  remote editing, signing, amend, force push, and arbitrary Git commands remain
  out of scope.
- Diff text, paths, branch names, and remote names remain untrusted provider
  content even after bounding and secret screening.
- `git.push` is not advertised by a production hosted session. Enabling it
  requires a contained authenticated HTTPS smart-Git fixture proving the exact
  compare-and-swap receipt without ambient credentials or executable helpers.
- A successful mutation returns a fresh opaque state reference when the
  resulting bounded state can be observed. Otherwise the next action must call
  `git.read_state`.
- Supporting SSH or another desktop platform requires a separate tested target
  adapter and an ADR amendment; this decision must not be used as evidence that
  those platforms passed.
