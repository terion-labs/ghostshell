# ADR 0028: Governed File Viewer observations

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0008](0008-file-provider-contract-and-local-semantics.md),
  [ADR 0012](0012-durable-file-provider-runtime.md),
  [ADR 0019](0019-one-action-agent-capability-broker.md)
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)
- Follow-on:
  [ADR 0030](0030-governed-file-viewer-mkdir-and-delete.md) adds the separate
  governed mkdir/permanent-delete mutation boundary without widening these
  observation requests.

## Context

The built-in agent must be able to inspect an authorized File Viewer without
receiving general filesystem authority. Existing provider-neutral panel
contracts already expose typed list, stat, and bounded preview operations, but
their ordinary UI requests include provider locations, page controls, and
provider-specific state that must not become model-controlled authority.

A provider profile can also be edited without changing its logical ID. If a
live panel resolved every request through the newest catalog generation, the
same profile ID, authority, and logical path could silently refer to a
different backend root after approval. Comparing returned location strings
would not detect that change.

## Decision

Asura adds three closed, read-only agent tools:

- `files.list`;
- `files.stat`;
- `files.read`.

All three use `ReadFiles` and are observations. The default policy is `Auto`,
but each request still passes through the broker, receives one exact
authorization, is consumed once by SessionHost, and has a complete durable
audit outcome. There is no separate agent filesystem client and no direct
provider SDK access in the agent runtime.

### Model-controlled input

The model supplies only a bounded `path_segments` array relative to the
trusted root. Exact panel/session schemas omit `panel_id`. Broad tab or
workspace schemas require one `panel_id` selected from the current
host-generated enum, even when only one File Viewer is eligible.

The model cannot provide a profile ID, authority, absolute path, root,
version, continuation token, page size, hidden-file flag, preview byte limit,
or provider option. Paths contain at most 64 printable, valid-Unicode segments,
255 UTF-8 bytes per segment and 4 KiB in total. Empty, traversal, separator,
control/format, and literal-secret-shaped segments fail before authorization.
`files.read` requires a non-root path.

The first slice is intentionally limited to versionless hierarchical
locations. Object-key and container-root panels continue to work for a human
through the ordinary File Viewer but advertise no governed file tool.

### Session-owned provider scope

`IFilePanelSession` publishes immutable `FileSessionMetadata` captured by the
production factory from the exact provider descriptor and initial panel
location. It contains the trusted root, provider capabilities, maximum list
page size, and maximum preview size. SessionHost copies that metadata into the
live session descriptor and context fingerprint; request payloads cannot
replace it.

`CatalogFileProviderRuntime` leases the complete adapter generation when the
File Viewer session opens. Panel list/stat/preview, ordinary panel mutations,
and transfer enqueue/retry use that leased generation until the session is
disposed. In-flight transfers hold their own generation lease. A catalog
refresh retires the old generation but cannot retarget the live session; a
newly opened panel receives the replacement generation. This is a
backend-identity invariant, not only a presentation cache.

Before a saved hosted panel binds, its connection selector continues to follow
catalog materialization only to find the exact saved profile. If that profile
is unavailable, the panel creates no host session and refuses selection of a
different profile, fallback to a provider root, or location editing. The first
hosted list binds the exact saved structured location and profile. Selection,
location, and navigation controls stay disabled while that first ensure is in
flight, and a failed ensure may retry only that same location.

Once SessionHost confirms the session, the picker freezes the corresponding
profile snapshot. If the catalog changes concurrently with binding, the picker
narrows to the exact initial provider reconstructed from returned trusted
metadata rather than presenting a profile that the pinned generation may not
own. Later catalog changes are visible in newly opened File Viewers.

### Operation bounds

`files.list` requests the first page only, with hidden entries disabled and a
page size equal to the lower of the provider limit and 100. Provider
continuation tokens and item versions are never returned to the model.
Remote protocols without stable server-side paging capture at most 100,000
observed entries and 8 MiB of aggregate UTF-8 name data, checking cancellation
per entry before sorting. SFTP and FTP enforce the ceiling while streaming
their transport enumeration; the common provider boundary recaptures custom
session output defensively. Exceeding either ceiling fails with a typed limit
error.

`files.stat` requests exactly the resolved relative location.

`files.read` requests a preview equal to the lower of the provider limit and
64 KiB. It accepts only strict UTF-8 `Text` or `StructuredText`; image, hex,
malformed UTF-8, oversized, and otherwise non-textual results fail closed.
The panel client writes into a fixed-capacity, non-growable destination and
validates the provider receipt's profile, authority, address, requested
version, offset, byte count, and actual destination length before publishing a
preview. A validated provider-returned version is preserved until SessionHost
checks and strips provider identity state. This is not an arbitrary stream or
whole-file API.

### Dispatch and hostile results

Immediately before dispatch, SessionHost re-resolves the exact graph owner and
session revision, recomputes the action binding, consumes the one-action
authorization, and rechecks both session and provider capabilities. It derives
the provider request from immutable metadata and typed path segments, links
caller, run/permit, and session-lifetime cancellation, and invokes only the
captured `IFilePanelSession`.

Provider results are untrusted. SessionHost rejects a list larger than the
requested page, hidden or malformed entries, locations outside the trusted
root, entries that are not direct children of the listed directory, stat or
preview locations that do not exactly match the request, oversized previews,
unsupported media types, and provider-controlled identity drift. It
reconstructs accepted locations from trusted request material, removes
versions and pagination state, and maps provider errors through a closed
stable-code allowlist.

The runtime serializes an escaped, size-measured projection with
`content_origin=untrusted_file`, explicit truncation/redaction counts, bounded
metadata, and no provider messages. Secret-shaped file names or content are
withheld or redacted before provider continuation. Raw file content, names,
paths, versions, continuation tokens, and provider errors are excluded from
durable audit.

## Consequences

- The agent can inspect the same hosted File Viewer session and immutable
  trusted root shown in the context inspector without general process or
  filesystem authority.
- Editing a provider profile cannot silently retarget an existing panel or an
  approved file observation.
- An asynchronously materialized saved panel cannot bind a distractor profile,
  fallback root, or user-edited location before its exact saved root exists.
- The provider-neutral structured-location model remains intact; no local-path
  concatenation is introduced.
- Large directories, hidden entries, binary previews, pagination, search,
  object-storage reads, and all file mutations remain outside this initial
  governed slice.
- A model must ask the user to use the ordinary File Viewer when the selected
  provider or location cannot satisfy the narrow observation contract.

## Alternatives rejected

- Giving the agent a local filesystem API would bypass panel scope, remote
  provider semantics, SessionHost lifecycle, and the capability broker.
- Accepting absolute paths, profile IDs, roots, limits, or continuation tokens
  from the model would turn data into authority and permit scope widening.
- Looking up the latest adapter generation for every operation would let a
  same-ID profile edit retarget a live session invisibly.
- Returning binary/hex previews or arbitrary provider messages would expand
  exfiltration and prompt-injection surface without being necessary for the
  first useful inspection slice.
- Adding rename, upload, delete, transfer, or write operations before this read
  boundary is proven would mix observation and mutation authority.
