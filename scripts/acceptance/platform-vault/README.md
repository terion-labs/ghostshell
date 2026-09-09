# Platform vault acceptance runner

This runner executes exactly the opt-in native-vault conformance test against the current desktop user's credential store. It supports macOS Keychain Services, Windows DPAPI, and Linux Secret Service (`secret-tool`).

The durable receipt is an allow-listed JSON document containing only sanitized OS, architecture, .NET SDK, provider, test-outcome, and cleanup metadata. Test stdout/stderr and TRX stay inside an isolated temporary run and are never copied into the receipt. A failed lifecycle, including a child-test timeout, preserves the synthetic service name, opaque `SecretRef`, and isolated metadata directory in `cleanup.recovery`; use those identifiers to remove only that test item. Before the child test starts, the same non-secret identifiers are written to `recovery.json` in the isolated `asura-platform-vault-*` directory. That manifest remains recoverable even if the runner itself is terminated before it can emit a receipt.

From the repository root:

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

Exit codes are `0` for `PASS`, `1` for `FAIL`, `2` for `BLOCKED`, and `64` for invalid runner arguments. `BLOCKED` is reserved for an unsupported host, an unavailable SDK/provider prerequisite, or a skipped test. A provider lifecycle assertion is `FAIL`.

Linux must have `secret-tool` plus an accessible desktop Secret Service session. Run Windows and Linux acceptance on named physical/self-hosted desktop environments; a cross-RID build or container is not native-vault acceptance.
