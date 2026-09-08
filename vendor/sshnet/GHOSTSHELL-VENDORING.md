# SSH.NET 2026.0.0

- Official repository: https://github.com/sshnet/SSH.NET
- Pinned commit: `7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e`, matching the installed 2026.0.0 NuGet package's repository metadata.
- Source archive: https://github.com/sshnet/SSH.NET/archive/7b2fd3dbf2c86a80a7b06cea020aa5f821c9902e.tar.gz
- Archive SHA-256: `e3c305e2bf41d00f7aba51b9cdd151567dfe32c616ad322561ac59e3d6a4593b`.
- License: MIT, retained at `upstream/LICENSE`; upstream third-party notices are retained.

Only the runtime source, signing key and notices are imported. GhostShell builds a net10.0-only project, with central pinned BouncyCastle and logging dependencies. `ThisAssembly.cs` supplies the pinned package version that upstream normally generates using Nerdbank. Assembly version and the upstream signing key preserve the `2026.0.0.1` assembly identity required by SshNet.Agent. The project package identity remains SSH.NET 2026.0.0 so transitive dependencies resolve this project, not a second stock DLL.

The integration project is `SSH.NET.csproj`, matching that required PackageId.
The SDK uses the project filename to recognize an existing assets-library entry;
matching it avoids a duplicate raw-reference dependency without changing the CLR
assembly name `Renci.SshNet` or SshNet.Agent's dependency substitution.

## Local change

`rfc1929-authentication.patch` is the only runtime source modification. It adds a credential-requiring `ForwardedPortDynamic` constructor. Such a port rejects SOCKS4 and unauthenticated SOCKS5, verifies RFC 1929 credentials before opening a destination channel, and retains only credential hashes. Existing constructor semantics remain available to other upstream callers. Invalid credential lengths fail before listening. Failed negotiation cannot proceed to a destination request.

This is a targeted change inside the pinned upstream protocol implementation, not a first-party module moved into vendor to avoid analyzers. Upstream source stays in the existing vendor compilation boundary; the factory integration, HTTP CONNECT broker, and signed socket tests remain first-party analyzed code. Formatter exclusion covers `upstream` only. No analyzer/banned-symbol policy was weakened.

The authenticated constructor additionally caps pending unauthenticated handshakes at 32. A slot is released after successful authentication or failure cleanup; long-lived authenticated streams do not retain slots. Existing finite socket-read timeouts and byte-length-bounded authentication frames are retained. The socket suite covers admission exhaustion, immediate reuse after authentication, and idle timeout.

First-party `tests/GhostShell.SshNet.Tests` uses upstream's existing signed test friend identity to attach mocked SSH Session/channel objects while testing real loopback socket negotiation. Moq is used for the large internal SDK interfaces; no reflection-based production access, additional friend assembly, real SSH service, or real credentials are involved. The tests verify authenticated success, no-auth/SOCKS4 rejection, wrong credentials, malformed authentication, and disposal of incomplete authentication.

To update: fetch and verify the pinned source, replace the exact imported source subset, apply the recorded patch, update package/assembly provenance together, regenerate dependency locks, and run both authenticated port and browser broker regression suites plus the repository full gate. Never replace only the DLL or reintroduce a stock transitive SSH.NET package.

`UPSTREAM-SOURCE.sha256` records pristine selected source hashes;
`SOURCE-SNAPSHOT.sha256` records patched source plus integration files, excluding
only root `bin`/`obj` output directories, root default and supported per-RID locks, and the root
snapshot inventory itself. Nested bin/obj directories remain source inputs;
explicit Compile globs can consume their files. Git attributes preserve exact
source/patch bytes including upstream CRLF. Paths are relative to this vendor
directory. Regenerate with `node scripts/update-managed-vendor-inventory.mjs sshnet /absolute/path/to/pinned-source.tar.gz`.
The generator verifies both archive checksum and exact patch application before
writing inventories; a source/patch mismatch fails rather than being attested.

The active source project uses `packages.lock.json` for the default graph and
`packages.<RID>.lock.json` for linux-x64, linux-arm64, osx-x64, osx-arm64, and
win-x64. Only these exact root lock paths are excluded from the source inventory;
the repository locked-restore guard rejects missing or redirected runtime locks.
Windows-managed and macOS-AOT properties reuse these locks only while repeated
locked restores prove their dependency graphs unchanged. No analyzer policy is imported.
