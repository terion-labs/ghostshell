# Asura platform vault acceptance

Native vault verification is opt in because it mutates the current user's operating-system credential store. A run uses only synthetic bytes, a unique service namespace, a fresh opaque `SecretRef`, and an isolated temporary metadata directory. It deletes the exact item in `finally`, zeroes copied byte buffers, and never prints secret material. If emergency deletion fails or the child test times out after creation, the runner emits the synthetic service/reference and preserves its isolated metadata for manual cleanup. Before launching the child test it also writes those non-secret identifiers to `recovery.json` inside the isolated `asura-platform-vault-*` directory, so an abrupt runner termination cannot strand an unidentified test item even though it cannot emit a receipt. The normal repository gate reports this case as **skipped** rather than silently counting an early return as a pass.

## Reproducible runner

Run from the repository root on the named desktop host whose credential store is being accepted:

```sh
./.dotnet/dotnet run \
  --project scripts/acceptance/platform-vault/Asura.PlatformVaultAcceptance.csproj \
  -- self-test

./.dotnet/dotnet run \
  --project scripts/acceptance/platform-vault/Asura.PlatformVaultAcceptance.csproj \
  -- run --receipt artifacts/platform-vault-acceptance/current-host.json

./.dotnet/dotnet run \
  --project scripts/acceptance/platform-vault/Asura.PlatformVaultAcceptance.csproj \
  -- validate artifacts/platform-vault-acceptance/current-host.json
```

The runner explicitly enables exactly the opt-in integration case, pre-generates its isolated recovery identifiers, persists the non-secret recovery manifest before execution, discards console output, requires one exact test result from TRX, and writes an allow-listed receipt conforming to [`receipt.schema.json`](../scripts/acceptance/platform-vault/receipt.schema.json). Receipts contain only sanitized OS, architecture, .NET SDK, provider, test-result, and cleanup metadata. They never contain test stdout/stderr, TRX, secret values, labels, or command lines.

Results are `PASS`, `FAIL`, or `BLOCKED`. `BLOCKED` is reserved for a missing platform prerequisite, unsupported host, unavailable .NET SDK, or skipped test. A vault lifecycle assertion is `FAIL`. On cleanup failure or an interrupted lifecycle, `cleanup.recovery` contains only the synthetic service name, opaque reference, and exact isolated metadata directory; the runner retains that directory and removes raw TRX. Use those identifiers to remove only the isolated test item, then remove the directory.

The test requires an OS-protected persistent adapter and all declared `ISecretVault` capabilities. It verifies create, duplicate rejection, purpose denial, metadata, scoped listing, resolve, last-used metadata, replace, relabel, cancellation, delete, post-delete `NotFound`, and an empty final listing. Linux additionally requires `secret-tool` and an accessible desktop Secret Service session.

## Evidence matrix

| Host | Adapter | Timestamp | Result | Notes |
| --- | --- | --- | --- | --- |
| macOS 26.5.2 (25F84), arm64, .NET SDK 10.0.302 | Keychain Services (`macos-keychain`) | 2026-07-22T23:02:35Z | **PASS** | Runner receipt [2026-07-22-macos-arm64.json](acceptance/platform-vault/2026-07-22-macos-arm64.json) validates; the 150 ms lifecycle passed with confirmed cleanup and no retained recovery metadata. |
| Windows 11 x64 named host | DPAPI-backed persistent vault | — | **OUTSTANDING** | Must run on Windows; a successful cross-RID build is not vault acceptance. |
| Supported Linux desktop named host | Secret Service / system keyring | — | **OUTSTANDING** | Requires a live supported keyring and `secret-tool`; container or unavailable-vault behavior is not native acceptance. |

This evidence covers the adapter contract only. It does not replace keyboard-only and assistive-technology acceptance of the Secrets settings UI.
