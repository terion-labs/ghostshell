# Database viewer integration tests

This project runs the production database client, database-panel view model, and rendered Avalonia database workspace against disposable databases. Each provider gets the same seeded logical schema and the same end-to-end conformance journey. The project remains in `Asura.slnx` so ordinary builds compile it, but its runtime tests are opt-in because several images are large.

## Provider matrix

| Provider ID | Harness | Scope |
| --- | --- | --- |
| `sqlite` | Temporary `.sqlite` file | Native SQLite engine and driver |
| `duckdb` | Temporary `.duckdb` file | Native DuckDB engine and driver |
| `postgres` | `postgres:18.4` | Native PostgreSQL |
| `cockroach` | `cockroachdb/cockroach:v26.2.4` | Native CockroachDB, single-node in-memory mode |
| `mysql` | `mysql:8.4.10` | Native MySQL LTS |
| `mariadb` | `mariadb:11.8.8` | Native MariaDB LTS |
| `sqlserver` | `mcr.microsoft.com/mssql/server:2025-CU7-ubuntu-24.04` | Native SQL Server Developer; the fixture requests `linux/amd64` explicitly |
| `oracle` | `container-registry.oracle.com/database/free:23.26.2.0-lite` | Native Oracle Database Free |
| `firebird` | `firebirdsql/firebird:5.0.4-trixie` | Native Firebird |
| `clickhouse` | `clickhouse/clickhouse-server:26.3.17.56` | Native ClickHouse metadata and browsing; mutations are asserted read-only |
| `redshift` | `postgres:18.4` through Asura's Redshift path | PostgreSQL-wire/dialect compatibility only; mutations are asserted read-only |
| `redis` | `redis:8.2-alpine` | Native Redis session: logical databases, SCAN, core/JSON/Time Series mutation, TTL, UNLINK, Pub/Sub, capability discovery, secondary indexes, and full-text search over RESP3 |

Amazon Redshift is a managed AWS service and this suite does not pretend that PostgreSQL is the Redshift engine. The `redshift` case verifies Asura's Redshift connection parsing, pgwire path, identifier/filter/query behavior, and intended read-only contract against a disposable PostgreSQL server. It is not native Redshift conformance; engine-specific verification needs an isolated managed Redshift endpoint. See AWS's notes on [Redshift and PostgreSQL differences](https://docs.aws.amazon.com/redshift/latest/dg/c_redshift-and-postgres-sql.html).

The file engines create unique files under the system temporary directory. Container cases use Testcontainers with random ports bound only to IPv4 loopback. Every case seeds its own database and disposes the file or container when finished.

Redis runs beside the relational conformance matrix rather than through it: the
Database panel shares durable connection and recovery infrastructure, while the
Redis integration test exercises its long-lived session contract directly.

## Operations covered

Each selected provider runs one complete workflow that exercises:

- driver lookup plus connection-string parse/rebuild and connection readiness;
- table and view discovery, schema-qualified names, and hostile quoted identifiers;
- structure metadata for primary keys, identity/default/generated/read-only columns, and indexes;
- database-wide Mermaid ER diagrams from real column and foreign-key catalogs, including a rendered Objects/ER Diagram switch and clipboard action in the database-overview UI;
- qualified and legacy table previews, maximum-row truncation, typed value materialization, and detached binary/large values;
- deterministic sorting and paging, including the 200-row UI boundary and keyless-table paging restrictions;
- every filter operator: equals, not equals, less/less-or-equal, greater/greater-or-equal, contains/not-contains, starts with, ends with, `IN`/`NOT IN`, is null, and is not null;
- multiple AND filters, literal `%`, `_`, and `!` handling, parameterized injection-shaped values, and rejection of unknown filter/sort columns;
- insert, update, and delete batches; explicit value, `NULL`, and `DEFAULT` states; rejection of identity/generated/non-nullable invalid edits; and empty changes;
- view, keyless-table, provider-read-only, and MySQL/MariaDB non-transactional-table mutation rejection;
- optimistic concurrency winner/stale-writer behavior and rollback of an insert bundled with a conflicting update;
- the real `DatabaseRuntimePanelViewModel`: connect, filter the object list, preview, Data/Structure/Indexes modes, next/previous page, apply/clear filter, edit/save, immediate navigation after save, add with `NULL`/`DEFAULT`, delete, revert, and connection/object lock state;
- an Avalonia headless journey for every provider through the actual runtime view: rendered object buttons and result columns, Data/Structure/Indexes controls, filter and paging controls, and connection/object enabled states; it physically clicks column headers and proves exact ascending/descending rows, accessible sort state, filter preservation, and the pending-edit sort guard; it opens the real cell context menu through `ContextRequested` and exercises bounded Quick Look layout and its keyboard apply path, clipboard copy, refresh, and quick filter; editable providers additionally exercise the real DataGrid editors, validation, grid commit, Paste/Add/Duplicate/NULL/DEFAULT/Delete actions, the rendered Save/Revert handlers, persisted mutations, and post-save navigation, while read-only providers prove every mutation action stays visible but disabled;
- raw SQL provenance through the rendered Run control: an edited generated preview with `ORDER BY` must retain exact table capabilities and persist two edit/save/reload cycles, while expression/alias projections stay read-only but retain safe outer-query sort/filter/refresh; SQL Server additionally rejects partial projections with hidden KeyInfo columns, same-name computed replacements, and swapped aliases;
- the real native Calcite SQL-language session, when required: a database-derived catalog is reused for schema-aware alias completion, valid and unknown-column diagnostics, each provider's exact production preview SQL, and default catalog/schema resolution; the rendered AvaloniaEdit control then proves the same live session produces a completion popup and diagnostic underline before `Ctrl+Enter` runs the preview;
- view-model conflict recovery, pending-change visibility, save/revert enablement, and recovery from invalid SQL for both the client and UI state machine.

Provider capabilities remain intentional: ClickHouse and the Redshift protocol case run the full read/metadata/UI workflow and assert mutations are disabled; editable providers additionally run all mutation and save journeys.

## Run locally

Install the repository-local SDK once if needed:

```bash
ASURA_SKIP_NATIVE=1 ./scripts/bootstrap.sh
```

Run every provider (Docker must be running):

```bash
./scripts/test-database-viewer-integration.sh
```

Run only file databases (Docker is neither checked nor required):

```bash
./scripts/test-database-viewer-integration.sh sqlite,duckdb
```

Run one container provider and pass additional `dotnet test` arguments:

```bash
./scripts/test-database-viewer-integration.sh postgres --logger "console;verbosity=detailed"
```

The commands above test the database viewer without requiring the optional
Calcite executable. To run the production SQL-intelligence boundary too, first
build the worker for the current host:

```bash
./scripts/build-sql-language-worker.sh
```

The build runs the Java provider matrix and a linked-native protocol smoke test,
then publishes the executable under `native/artifacts/<host-rid>/`. Require that
artifact in the database and rendered-editor journey by setting both variables:

```bash
ASURA_RUN_SQL_LANGUAGE_NATIVE=1 \
ASURA_SQL_LANGUAGE_WORKER="$PWD/native/artifacts/osx-arm64/asura-sql-language" \
./scripts/test-database-viewer-integration.sh sqlite,duckdb
```

Replace `osx-arm64` with the RID produced for the current host, such as
`osx-x64`, `linux-x64`, or `linux-arm64`. In required-native mode the wrapper
fails before starting a database if the worker path is missing, is not a file,
or is not executable. The tests also fail if the process cannot initialize, so
the opt-in cannot silently degrade to completion-free editing.

The wrapper sets the two opt-in variables used by the tests:

- `ASURA_RUN_DATABASE_INTEGRATION=1` enables runtime integration cases.
- `ASURA_DATABASE_INTEGRATION_PROVIDERS=all` or a comma-separated list selects provider IDs.

They can also be set directly when invoking the integration project with `./.dotnet/dotnet test`. Provider IDs are `sqlite`, `duckdb`, `postgres`, `cockroach`, `redshift`, `mysql`, `mariadb`, `sqlserver`, `oracle`, `firebird`, `clickhouse`, and `redis`.

The test assembly disables parallel execution so a local all-provider run starts and tears down the databases sequentially. CI builds and smoke-tests the `linux-x64` worker once, publishes and executes the production .NET client as a real Native-AOT binary both with and without that worker, and distributes the tested executable to all 12 provider jobs. Every CI provider job sets required-native mode, runs the standalone native client lifecycle, then runs its selected database journey. The suite is sharded one provider per job on relevant pull requests, on manual dispatch, and on the weekly scheduled run.
