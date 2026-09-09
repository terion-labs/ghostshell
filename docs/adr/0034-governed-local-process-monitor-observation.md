# ADR 0034: Governed local Process Monitor observation

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0019](0019-one-action-agent-capability-broker.md) and
  [ADR 0029](0029-scope-clipped-governed-workspace-graph-observations.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

Asura already has a hosted Process Monitor panel backed by the local
`SystemProcessSnapshotSource`. Its human UI deliberately omits command lines,
users, environments, open files, and terminal content. The native agent needs
a bounded way to observe that same panel without receiving a general process
API, a process launcher, or authority over the remote machine behind an SSH,
Docker, or WSL terminal.

Treating a terminal's connection boundary as process-monitor authority would be
false. The current monitor samples the machine running Asura. It does not
execute a command in a terminal, shell out locally, install software remotely,
or discover processes through a connection adapter.

Process metadata is also hostile input. A process name can contain unsafe
Unicode or secret-shaped material, process enumeration can race, and a native
source can return malformed counts or measurements. The observation therefore
needs the same explicit scope, policy, cancellation, audit, and result bounds
as the other governed tools.

## Decision

Asura adds one read-only application tool:

- `processes.list`.

It maps to `AgentCapability.ProcessControl` with
`AgentActionRisk.Observation`. The capability remains `Off` in the default
policy. When enabled, every call still uses the normal one-action
capability-broker and SessionHost path and receives a complete durable action
audit chain. There is no direct process API in the agent runtime.

### Scope and model-controlled input

The tool is available only when the immutable run scope currently contains an
active hosted `ProcessMonitor` panel whose live session advertises
`processes.list`.

For an exact panel scope, the closed schema contains only optional `sort` and
`limit` fields. For a Workspace or internal `OpenTab` scope, the same schema
additionally requires exactly one `panel_id` selected from the fresh host-enumerated enum of
eligible in-scope Process Monitor panels. The `panel_id` remains required when
only one panel is eligible. An exact schema rejects `panel_id`.

The allowed sort values are:

- `cpu_desc`;
- `memory_desc`;
- `name_asc`;
- `pid_asc`.

The allowed limits are `16`, `32`, and `64`. Omitted values mean
`sort=cpu_desc` and `limit=32`. The model cannot provide a session ID, PID or
name filter, arbitrary limit, offset, continuation token, command, connection,
host selector, or remote-execution option.

The trusted action composer binds the exact window, workspace, tab, panel,
session, graph/session revision, capability set, requested sort, and requested
limit into the proposal identity and argument digests. Approval presentation
identifies the Process Monitor as the local host and shows the exact bounded
query.

### Dispatch and cancellation

SessionHost resolves and binds the exact panel under its graph gate, consumes
one authorization, and captures the typed `IProcessMonitorPanelSession`. It
then releases the graph gate before awaiting the native process sample.

The capture is linked to caller cancellation, broker permit revocation, and
the Process Monitor session's close lifetime. Closing the panel interrupts a
waiting or in-flight capture where the platform source supports cancellation.
SessionHost invokes `ListProcessesAsync` exactly once.

After capture, SessionHost reacquires the graph gate and re-resolves ownership,
panel kind, session identity, revision, and capabilities. Drift discards the
sample and fails closed. Because the operation is read-only, a discarded
sample is a definite failure rather than an outcome-unknown mutation.

### Hostile result boundary

SessionHost projects the untrusted monitor snapshot through an Application
contract before provider continuation. A result contains at most 64 process
rows and at most 64 KiB of actual escaped JSON. It carries the fixed
`content_origin=untrusted_local_process_metadata` marker, UTC capture time,
requested sort and limit, returned/enumerated/observed counts, truncation and
name-redaction metadata.

Each row may contain only:

- nonnegative PID;
- bounded safe display name and explicit redacted/truncated flags;
- finite CPU percentage from `0` through `100`, or null;
- nonnegative working-set bytes, or null;
- UTC start time, or null;
- whether the row represents Asura.

The projection rejects invalid timestamps, non-finite or out-of-range
measurements, negative or inconsistent counts, duplicate PIDs, excessive rows,
and malformed collections. A process display name uses strict Unicode, rejects
control, format, line-separator, and paragraph-separator code points, and is
truncated on a rune boundary to 128 UTF-8 bytes. Path-like, secret-shaped,
blank, or malformed names become one fixed redaction.

Command line, executable path, username, environment, open files, cumulative
processor time, source-specific identifiers, native error messages, and
terminal content never cross the boundary. Errors are mapped to a closed
stable-code allowlist.

### Audit and presentation

Audit records the usual requested, decision, started, and terminal phases with
the exact action bindings, result code, duration, and successful returned-row
count. It never stores process names, PIDs, measurements, source counts, raw
snapshots, provider JSON, or native errors. Completion-audit reconciliation
may retry only the immutable completion event; it never captures processes
again.

The desktop permits an active hosted Process Monitor as an exact agent target.
Its context inspector says `Process Monitor`, `local host`, and
`processes.list`. This presentation must not imply that the tool observes the
machine behind an active terminal connection.

## Consequences

- The native agent can inspect the same bounded local process view already
  visible to the user without a Node sidecar or software on another machine.
- Process observation is unavailable by default and cannot bypass the
  capability broker when enabled.
- A local panel close, run stop, policy change, deadline, or target drift
  cancels or discards the observation without retaining process metadata.
- Remote process observation remains a separate future capability. It must use
  an explicit connection/session service and cannot silently reuse this local
  tool.

## Alternatives rejected

- Running `ps`, PowerShell, WMI, or another command through a terminal would
  conflate terminal command authority with Process Monitor observation and
  could target the wrong machine.
- Calling `System.Diagnostics.Process` from the agent runtime would bypass the
  hosted panel, broker, graph scope, cancellation, and audit boundaries.
- Returning command lines, paths, users, or environment values would add a
  high-value secret and prompt-injection channel without being necessary for
  the bounded diagnostic use case.
- Treating `ProcessControl=Auto` as a product default would expose local
  process metadata without an explicit policy choice.
