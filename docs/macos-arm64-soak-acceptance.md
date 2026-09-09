# macOS arm64 release soak acceptance

Issue #38 is implemented as a human-assisted, machine-measured release soak suite for one local macOS arm64 reference system. Windows, Linux, and a multi-host matrix are deferred. Executing the suite against the exact signed release candidate remains a release-signoff task in #47.

## v1 release policy

[`policy.v1.json`](acceptance/macos-arm64-soak/policy.v1.json) is the concrete v1 policy. Its values are conservative practical starting limits: 15–30 minutes per scenario, explicit work counts, zero tolerated product failures, zero retained captured processes, 30–60 second cleanup deadlines, and 256–384 MiB aggregate process-tree RSS growth ceilings. Changing a budget is a policy change: update the checked-in policy, record the review rationale, and rerun the validator and tests. A release operator must not supply ad hoc command-line thresholds.

The catalog covers reconnect/reattach, clean and abrupt startup recovery, many panels, bounded scrollback, provider failure/non-cooperation, CEF renderer replacement, MCP cleanup, sleep/wake, Quick Terminal cycles, and native-view lifecycle. The runner performs the single expected abrupt package exit through the process handle it launched. All other failure injection remains operator-driven through normal product/developer controls.

## Run

Build or obtain the exact `Asura.app`, then validate and execute with the pinned SDK:

```bash
./.dotnet/dotnet run --project tools/Asura.SoakAcceptance -- validate-policy docs/acceptance/macos-arm64-soak/policy.v1.json
./.dotnet/dotnet run --project tools/Asura.SoakAcceptance -- run \
  --package /absolute/path/to/Asura.app \
  --build-label release-candidate-id \
  --policy docs/acceptance/macos-arm64-soak/policy.v1.json \
  --evidence-dir artifacts/soak-acceptance
```

Use a local interactive arm64 Mac on AC power where practical. The runner rejects redirected input/output, non-macOS, and non-arm64 execution. Follow each fixed instruction, enter only bounded integer counts and `PASS`, `FAIL`, or `BLOCKED`, and close the app normally when requested. A missing observation, insufficient load, excess failure, RSS growth over budget, sampling error, unexpected exit, retained captured process, operator failure, or changed package fingerprint fails closed.

## Measurement and evidence

Once per second, the runner revalidates stable macOS process identities and samples aggregate working set, CPU time, and live process count for the launched package tree. It retains every observed stable identity through cleanup and requires zero live captured identities within the scenario deadline. RSS growth is the non-negative difference between the first and final aggregate samples; peak RSS and process count are also recorded. This combines native children (including CEF and MCP processes) and managed application memory; it intentionally does not claim unsupported native-versus-managed heap attribution. When a budget fails, Instruments or `dotnet-trace` may diagnose attribution, but diagnostic output is not accepted as a release receipt.

After all scenarios, the package is fingerprinted again. The receipt passes only when all machine-constrained operator results pass and the complete package manifest is unchanged. The runner writes exactly `receipt.json`, deterministic `receipt.md`, and `receipt.json.sha256` in a new directory. The JSON and policy schemas are checked in beside the policy.

Receipts contain only package/build hashes, a truncated one-way host-name hash, reference configuration ID, OS/architecture, power-source category, timestamps, counters, and stable failure codes. They never contain usernames, raw host names, arguments, environment dumps, commands, paths, URLs, terminal/provider/MCP content, credentials, or free-form notes. Recovery checks explicitly require that a crash neither fabricates success nor widens approval or authority.

## Release failure policy

A nonzero runner exit, `fail`, `blocked`, schema mismatch, digest mismatch, modified package, missing scenario, or receipt from another package/host/policy is not release evidence. Do not average failures away or rerun only a failed scenario. Fix the product or documented harness defect and rerun the entire fixed catalog on the same reference configuration and exact candidate. #47 owns attaching and reviewing that signed-candidate receipt before release sign-off.
