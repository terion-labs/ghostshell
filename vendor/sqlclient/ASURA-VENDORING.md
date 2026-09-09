# Test-only SqlClient protocol fixtures

Production uses the unmodified `Microsoft.Data.SqlClient` 6.0.2 NuGet package.
No driver implementation, patched transport, or production source project is
vendored here. Routed database operations run the stock driver in the selected
connection backend; routing is enforced at the service VM's network boundary.

`tests/upstream` retains only Microsoft's TDS test-server tooling from commit
`b16dec0a5622fd5b3d5311191bac4cafadc43e60`:
https://github.com/dotnet/SqlClient/tree/b16dec0a5622fd5b3d5311191bac4cafadc43e60.
The original archive SHA-256 is
`f4c2cfd1a7a48f5e4f0b6b97639e5c17730fca1eade31668732280b82df21870`.
The MIT license is retained at `../../licenses/SQLCLIENT-MIT.txt`.

`tests/README.md` describes the test-only assembly, its synthetic certificate,
and the bounded header-fragmentation repair in `tests/header-fragmentation.patch`.
First-party test code retains the repository analyzer policy. The local
`Directory.Build.props` applies only to the upstream test assembly and preserves
the repository's locked-restore checks.

`UPSTREAM-SOURCE.sha256` preserves the original hashes for the retained upstream
test files. `SOURCE-SNAPSHOT.sha256` records the current retained source, patch,
project, and provenance files. Build outputs and dependency locks are not source
snapshot inputs; locked restores validate the locks independently. Verify the
current snapshot from this directory with `shasum -a 256 -c SOURCE-SNAPSHOT.sha256`.
