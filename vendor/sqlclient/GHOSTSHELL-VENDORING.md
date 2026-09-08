# Pinned SqlClient source boundary

Upstream is Microsoft.Data.SqlClient 6.0.2, commit
`b16dec0a5622fd5b3d5311191bac4cafadc43e60` from
https://github.com/dotnet/SqlClient/tree/b16dec0a5622fd5b3d5311191bac4cafadc43e60.
The source archive SHA-256 is
`f4c2cfd1a7a48f5e4f0b6b97639e5c17730fca1eade31668732280b82df21870`.
`upstream/LICENSE` retains the MIT license.

The source subset contains the shared provider sources, original netcore source
sets, and XML documentation snippets. The build retains the original explicit
conditional compile/resource lists in `Upstream.Items.props`, C# 9, and net9.0.
The project remains under the original netcore/src directory because upstream
XML documentation includes resolve relative to that compiler working directory.
No netfx source set, Linux executable, guest filesystem, or platform runtime is
introduced into the macOS application by this source dependency.

`Strings.ResourceNames.cs` is generated from the original Strings.resx using the
same 1,542 resource names as the upstream PowerShell generator. The public strong
name key preserves the package assembly identity, version 6.0.0.0 and token
23ec7fc2d6eaa4a5. Public signing does not claim Microsoft's private signature.
The declared upstream component version remains 6.0.2.

The local integration PackageId is `Microsoft.Data.SqlClient.Routed`, matching
`Microsoft.Data.SqlClient.Routed.csproj`. This prevents the SDK from mistaking an
already restored project for an additional raw reference. The upstream component
remains Microsoft.Data.SqlClient 6.0.2; the CLR name/version are unchanged. Unlike
SSH.NET, no third-party dependency in the application requires the upstream
SqlClient PackageId for package-to-project substitution. Catalog evidence records
the exact local dependency identity and upstream component identity separately.

Regenerate the resource integration source with the repository SDK:

```sh
./.dotnet/dotnet run --file scripts/generate-sqlclient-resource-names.cs -- vendor/sqlclient/upstream/src/Microsoft.Data.SqlClient/src/Resources/Strings.resx vendor/sqlclient/Strings.ResourceNames.cs
```

The package-free generator verifies the pinned resx checksum, emits deterministic
UTF-8/LF source, and preserves upstream identifiers and values without renaming.
Its generated-code header describes that provenance; it is not a formatter
exclusion for the handwritten driver, transport adapters, or tests. Regenerate
the source inventory afterward using the command below.

The provider patch adds per-SqlConnection TCP transport and pool identity. It
preserves logical TLS/SPN identity, threads the transport through parser retry
and redirect creation, selects managed SNI per routed Windows parser, and refuses
routed host-side discovery or alternative protocols. Direct behavior remains on
the original provider path. A separate one-line correction changes the stale
DeprecatedKeywordsCount from five to four without changing any accepted mapping
or disabling its Debug assertion.

The transport must compile inside the provider because physical SNI handling
uses its internal TimeoutTimer. This small extension and its call sites are an
explicit upstream patch boundary, not an excuse to exclude GhostShell adapters
or tests from first-party analyzers. Vendor build policy follows the original
source's existing trim-warning declarations; new first-party code retains the
repository's full warning and analyzer policy.

Integration and exact verification status are recorded in
`.security/remediation-2026-09-07/sqlclient-routed-transport.md`.

`routed-transport.patch` records the exact production source/build-project delta;
`tests/header-fragmentation.patch` records the separate test-support repair.
`UPSTREAM-SOURCE.sha256` hashes the pristine pinned files selected into this
subset, while `SOURCE-SNAPSHOT.sha256` hashes the imported/patched source and
local integration files. Only exact output directories `bin`, `obj`, `tests/bin`,
`tests/obj`, and `upstream/src/Microsoft.Data.SqlClient/netcore/src/{bin,obj}` are
excluded. Only the two active projects' default and supported per-RID lock paths and the root
snapshot inventory itself are excluded as files; dependency locks are verified
by the repository gate. Nested directories merely named bin/obj elsewhere remain
source inputs, including files consumed by explicit recursive Compile globs.
Git attributes preserve exact source/patch bytes, including upstream CRLF.
Regenerate these derived files from the verified archive, without network access:

```sh
node scripts/update-sqlclient-provenance.mjs /absolute/path/to/pinned-source.tar.gz
```

The generator verifies the archive checksum before extraction and uses a private
temporary directory, removed afterward. Test-only TDS support is documented in
`tests/README.md` and is not a shipped runtime dependency.

The routed source project and test-support project select `packages.lock.json`
for the default graph and `packages.<RID>.lock.json` for linux-x64, linux-arm64,
osx-x64, osx-arm64, and win-x64. These exact filenames are excluded only under
`upstream/src/Microsoft.Data.SqlClient/netcore/src` and `tests`. The repository
locked-restore guard rejects missing or redirected runtime locks without importing
first-party analyzer policy. Windows-managed and macOS-AOT properties reuse these
locks only while repeated locked restores prove their dependency graphs unchanged.
