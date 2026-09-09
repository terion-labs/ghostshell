# ADR 0048: Governed local Docker container lifecycle

## Status

Accepted — 2026-08-26.

## Context

Asura already exposes bounded Docker observations and user-operated
container lifecycle buttons. Model-originating lifecycle changes need a
narrower boundary: a container ID can be reused or its state can change after
approval, cancellation can race daemon dispatch, and provider error text can
contain daemon endpoints or host details. A general Docker exec request is an
arbitrary command boundary even when argv is passed without a shell.

Windows and Linux desktop porting is deferred. Remote Docker targets have not
proved equivalent dispatch and reconciliation semantics. Process Monitor rows
also do not provide a handle-addressed process identity on macOS: PID plus
sampled start time cannot close the PID-reuse race before a signal.

## Decision

Expose six closed lifecycle tools only for a live local macOS Docker session:
`docker.container_start`, `docker.container_stop`,
`docker.container_restart`, `docker.container_pause`,
`docker.container_resume`, and `docker.container_remove`. Every tool requires
the existing `Docker` capability and has Destructive risk, so ordinary Auto
policy still requires explicit one-action approval.

`docker.read_state` returns an opaque container reference, exact engine
generation, normalized container state, and a one-shot opaque container
revision. A lifecycle request binds all four values into its authorization
digest. SessionHost re-resolves the exact graph/session/binding/capability, and
the Docker session atomically consumes the revision, refreshes engine state,
and compares the full immutable container ID plus stable image, compose, and
state fields immediately before dispatch. Relative creation-age display text
is intentionally excluded because it changes between equivalent snapshots.

The adapter accepts only a typed action and emits fixed argv. Stop and restart
use a fixed ten-second timeout. Remove is limited to a created, exited, or dead
standalone container and never uses force or volume removal. Start accepts
created or exited; stop, restart, and pause accept running; resume accepts
paused. No provider command output crosses the boundary.

Zero exit is `Applied`. A proven launch failure is `NotDispatched`. Timeout,
cancellation, connection loss, nonzero exit, or an exception after dispatch may
have begun is non-retryable `docker_mutation_outcome_unknown`. The runtime
stops the remaining proposal batch and requires a fresh `docker.read_state`;
it never implicitly replays the lifecycle action.

No Docker exec, shell path, executable, arguments, environment, user, working
directory, daemon endpoint, force flag, timeout choice, compose batch, prune,
build, pull, push, or copy mutation is admitted. Interactive access remains a
separately hosted Terminal panel with its own input and audit boundary.

Process mutation remains absent. macOS support requires a retained
handle-addressed identity primitive or another design that closes PID reuse;
Windows/Linux process-control claims and remote process control require their
own platform work and evidence.

## Consequences

- User-operated Docker controls retain their existing behavior; only model
  actions use this governed boundary.
- External Docker clients can still change daemon state in the narrow interval
  between refresh and command dispatch. The full-ID and revision guard prevents
  stale Asura authority but is not a daemon transaction.
- SSH Docker sessions and non-macOS sessions advertise no lifecycle tools.
  Their absence is `notApplicable` under the porting-deferred scope, not passed
  platform evidence.
- Adding remote control, another OS, process signaling, or Docker exec requires
  a separate tested adapter and an ADR amendment.
- `DockerLiveSmokeTests.GovernedLifecycleControlsOneExactDisposableLocalContainer`
  is an opt-in production-adapter check. It requires
  `ASURA_RUN_DOCKER_LIFECYCLE_INTEGRATION=1` and an already-present,
  long-running image in `ASURA_DOCKER_LIFECYCLE_IMAGE`; it never pulls.
  The test creates one random labeled container, verifies its full 64-hex ID
  and each state/receipt through start, restart, pause, resume, stop, and
  remove, then force-cleans only that owned ID/name in `finally`.

## Validation evidence

On 2026-08-26 the opt-in test passed against the local macOS Docker Desktop
daemon (`29.4.0`, Linux/arm64 engine) using the already-present `redis:latest`
image and no pull:

```sh
ASURA_RUN_DOCKER_LIFECYCLE_INTEGRATION=1 \
ASURA_DOCKER_LIFECYCLE_IMAGE=redis:latest \
./.dotnet/dotnet test \
  tests/Asura.Docker.Tests/Asura.Docker.Tests.csproj \
  --no-restore -c Release \
  --filter 'FullyQualifiedName~GovernedLifecycleControlsOneExactDisposableLocalContainer'
```

Result after the complete start/restart/pause/resume/stop/remove sequence:
`Passed: 1, Failed: 0` in 38 seconds. A post-run label query found no
remaining lifecycle-integration container. The same run exposed and corrected
an invalid guard on Docker's relative creation-age display text; the final
revision comparison uses immutable full ID plus stable image, compose, and
state fields.
