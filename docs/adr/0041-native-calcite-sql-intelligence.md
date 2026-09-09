# ADR 0041: Native Calcite worker for SQL editor intelligence

- Status: Accepted
- Date: 2026-08-09
- Builds on: the Application/Infrastructure dependency boundary in
  [architecture.md](../architecture.md)

## Context

The database workspace needs schema-aware SQL completion and validation without
making the desktop process depend on a JVM or a second CoreCLR. Asura's
production desktop is also intended to support .NET Native AOT. Loading Apache
Calcite through IKVM would place a large Java compatibility runtime and a broad
reflection surface inside that process, while translating Calcite to a managed
assembly would not remove those runtime and trimming constraints.

Calcite's parser, advisor, and validator can instead be compiled by GraalVM
Native Image. The SQL editor needs only those language services; it does not
need Calcite's planner, query execution, JDBC connections, or database
credentials.

## Decision

### An isolated native worker

Each supported runtime identifier ships a native executable named
`asura-sql-language` (with `.exe` on Windows), built from Calcite and a
small Asura Java wrapper using GraalVM Native Image:

```text
runtimes/<rid>/native/asura-sql-language[.exe]
```

The .NET Native-AOT-compatible client starts one worker process for each active
database editor session. The worker is optional: a missing payload, startup
failure, timeout, protocol violation, or crash disables completion and
validation for that editor without preventing database browsing or query
execution. A crash is isolated from the desktop process; the client may restart
the worker once and restore its detached catalog.

The executable is preferred over a Graal-generated shared library. Stdio has
negligible cost at editor interaction rates, while a process boundary gives us
crash isolation, simpler SubstrateVM heap ownership, simpler versioning, and no
cross-runtime allocator or isolate lifecycle in the desktop process.

### A small versioned protocol

The client and worker exchange UTF-8 JSON messages framed by a four-byte
big-endian length. Version 1 supports:

- `initialize`, with the initial detached catalog;
- `updateCatalog`, replacing that catalog atomically;
- `complete`, with SQL text and a UTF-16 cursor offset;
- `diagnose`, with SQL text; and
- `shutdown`.

Requests and responses carry matching version and request identifiers. Frames
are bounded to 8 MiB, requests are serialized per session, and malformed,
oversized, mismatched, or late messages fail closed. Caller cancellation or a
timeout terminates the worker because a response left in the byte stream could
otherwise be mistaken for the next request.

Completion replacement ranges and diagnostics use UTF-16 offsets so they map
directly to AvaloniaEdit and .NET strings. Every range returned by the worker is
validated against the originating document before presentation.

### Metadata, not authority

The worker receives a provider-neutral `SqlCatalogSnapshot` containing:

- driver identifier and preferred catalog/schema;
- exact table and view identifiers;
- exact column names, provider type names, semantic value kinds, and
  nullability;
- expression-callable routines, including qualified identity, overload
  signature, input modes, optional and variadic parameters, bounded arity,
  and return type; and
- explicit coverage plus server-reported intrinsic symbols that can
  corroborate Calcite operators without inventing new SQL constructs.

It never receives a connection string, credential, tunnel, provider object, or
live database connection. Asura reads the snapshot through the existing
database client and refreshes it after connection changes and successful
schema-changing statements. Names retain their exact provider-reported case;
driver profiles select Calcite quoting and casing rules.

Function completion is metadata-derived rather than maintained as an Asura
name list. The active Calcite dialect library supplies parser/operator
semantics, while the connected server's detached routine and intrinsic
catalogs establish availability and invocation identity. Complete provider
metadata suppresses uncorroborated Calcite suggestions. Partial or unavailable
metadata is shown as a catalog limitation; selected Calcite dialect-library
callables may fill that explicitly limited gap, but uncorroborated SQL-standard
functions and bare value expressions do not. Bare constructs such as
`CURRENT_TIMESTAMP` require positive server intrinsic evidence. Same-name
operators from multiple Calcite libraries are resolved in active-dialect,
SQL-standard, then semantic-equivalence order and otherwise fail closed.

The snapshot is advisory. Query execution remains exclusively in the existing
database client and continues to apply its provider, tunnel, mutation, and
concurrency rules. Calcite diagnostics never authorize or execute SQL.

### Editor behavior

The existing AvaloniaEdit-backed SQL control remains the source of truth for
text, selection, undo, accessibility, and the database workspace's Run
shortcut. When an available language session is attached it adds:

- debounced, generation-fenced validation with inline diagnostic marks;
- completion on `Ctrl+Space`/`Command+Space` and after a dot;
- schema/table/view/column/function/keyword completion details; and
- safe dismissal or replacement when the document, session, or catalog
  changes.

The language layer must not consume `Ctrl+Enter`/`Command+Enter`; those remain
the query execution shortcut. Empty documents and partially invalid SQL are
normal editor states and must not surface unhandled errors.

### Build and release

The Java wrapper pins Calcite and every build plugin/dependency in Maven. JVM
tests exercise the parser/advisor/validator and the framed protocol before the
native build. Graal reachability metadata is checked in with the worker and is
validated by executing the produced native binary, not merely by successfully
linking it.

Native binaries are built on their target operating system and architecture,
staged under `native/artifacts/<rid>`, then copied into the desktop's runtime
payload. Release builds must retain the Calcite/Graal dependency and license
closure, and macOS/Windows payloads participate in the same signing and
notarization process as the containing application.

The Java sources target Java 21 and the release linker is pinned to GraalVM
Native Image 25.0.4. The supported payloads are Linux x64/arm64, macOS
x64/arm64, and Windows x64; GraalVM does not currently provide the required
Windows arm64 Native Image toolchain. Builds publish a binary, resolved runtime
dependency list, third-party notices, and a hash-bearing receipt as one
smoke-tested closure. Packaging rejects a binary whose RID, file type, or hash
does not match that receipt. The receipt also records numeric legal-closure,
dependency, document, and review-required counts plus hashes of the dependency
manifest and third-party notices; release packaging re-hashes both legal files
rather than trusting their filenames. Platform receipts also bind the
executable ABI and compatibility floor. The macOS packager compares the
receipted minimum OS version with the Mach-O `LC_BUILD_VERSION` command and
rejects a worker that requires anything newer than Asura's macOS 13
deployment target.

Release publishing must opt into the desktop project's
`AsuraSqlLanguageRequired=true` gate. Ordinary developer builds may omit
the worker, but a gated publish fails when the executable, dependency list,
notices, or receipt is absent. The repository's portable release gate currently
builds complete `linux-x64` and `linux-arm64` payloads. The macOS arm64 candidate
has its separate target-host packager. Windows x64 remains excluded until a
target-host GraalVM worker build is part of its release lane; Windows arm64 is
explicitly unsupported because GraalVM Native Image has no corresponding
toolchain. `osx-x64` likewise has no release-candidate lane yet. None of these
excluded RIDs may be uploaded as a completion-free release candidate.

The live database matrix remains the compatibility gate for catalog casing,
qualification, type projection, aliases, joins, CTEs, quoted identifiers,
completion, and diagnostics across every supported driver. A Native AOT publish
and execution probe compiles the exact production client sources separately
from older Infrastructure code that is still being migrated away from
reflection-based JSON.

## Consequences

- The production desktop needs neither a JVM nor IKVM/CoreCLR compatibility
  runtime for SQL intelligence.
- Calcite crashes cannot corrupt desktop memory. The worker receives no
  credentials through its protocol or inherited environment, although it is
  still an ordinary same-user process and is not an operating-system sandbox.
- Each RID gains a roughly tens-of-megabytes native payload and a small worker
  process per connected database editor. Measured macOS RSS was about 79 MiB
  after repeated completion against a 10,000-table synthetic catalog, so many
  simultaneously connected panels scale memory linearly; suspending inactive
  sessions or a bounded worker pool is a follow-up before broad large-workspace
  deployment.
- Catalog initialization is proportional to schema size. The v1 whole-catalog
  protocol deliberately fails closed above its frame bound. Host extraction is
  bounded to 1,000 whole objects, 50,000 columns, 5,000 routines, 20,000 routine
  parameters, 5,000 intrinsic symbols, an estimated 6 MiB, and 15 seconds; the
  UI marks such a snapshot as limited. Incremental or lazy catalogs can be added
  as a later protocol version if large installations require them.
- Calcite is not the authoritative parser for all provider extensions. Parser
  failures therefore fail open, while v1 displays only catalog-safe semantic
  findings such as a provably unknown or ambiguous column. This deliberately
  misses some errors rather than painting provider-valid SQL red; diagnostics
  remain advisory and never block Run.
- Graal reachability metadata must evolve with Calcite and with the complete
  test corpus. A native image that only passes a toy query is not a releasable
  worker.

## Alternatives rejected

- **IKVM-compiled Calcite.** A portable managed assembly still brings Java
  runtime semantics, reflection/trimming risk, and a large in-process failure
  surface into the Native AOT application.
- **A Graal shared library with a C ABI.** It saves only the small stdio cost
  while making the desktop own SubstrateVM isolates, threads, allocator
  boundaries, and native crash behavior.
- **A bundled JVM worker.** This preserves process isolation but ships a much
  larger runtime and has materially slower cold startup.
- **A provider-specific handwritten SQL parser.** It duplicates a difficult
  language problem, would drift across eleven providers, and cannot provide
  Calcite's mature advisor and semantic validator.
- **Sending a live database connection to the worker.** This expands the trust
  boundary and is unnecessary for editor intelligence; detached metadata is
  sufficient.
