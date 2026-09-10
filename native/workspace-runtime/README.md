# Workspace SDK runtime

One signed `workspace-runtime serve --config /absolute/config.json` process owns
one persistent workspace VM. It uses Apple Containerization 0.42.0 directly and
never invokes Apple's `container` CLI. The only NIC attachment is a connected
Unix datagram socket, not vmnet/NAT. See `Contract.swift` for configuration types.

Commands:

- `prepare --image OCI_REFERENCE --rootfs ABSOLUTE_PATH --state-directory ABSOLUTE_PATH`
  unpacks a fully qualified image reference to a new 32 GiB sparse ext4 disk.
  It publishes the completed disk atomically and refuses to overwrite a disk.
- `prepare-initfs --image OCI_REFERENCE --output ABSOLUTE_PATH --state-directory ABSOLUTE_PATH`
  produces the SDK vminit ext4 image. Release tooling supplies a digest-pinned reference.
- `serve --config ABSOLUTE_PATH` emits `READY v1` after VM and initial process start.
- `exec --socket PATH --request BASE64_JSON` streams standard I/O and returns the
  guest exit code. The exec request has `arguments`, `environment`,
  `workingDirectory`, `userID`, `groupID`, optional `supplementaryGroups`,
  `terminal`, `columns`, `rows`. Terminal mode forwards SIGWINCH resizing.
- `network --socket PATH --packet-socket PATH` reads exactly 32 authentication
  bytes followed by EOF from stdin. It emits `READY v1` when the host Ethernet
  gateway starts and remains alive for the route lease. Killing this CLI closes
  its control socket, terminating the route. Credentials never enter argv.
- `status --socket PATH` returns readiness. `stop --socket PATH` stops the VM,
  preserving its disk.

Control sockets require owner-only parent directories and mode 0600; peer UID is
checked. Each socket handles one operation. Records are newline-delimited JSON,
bounded at 1 MiB. Byte fields use JSON base64 encoding. Initial records:

```
{"operation":"exec","exec":{...}}
{"operation":"network","packetSocketPath":"...","data":"BASE64_KEY"}
{"operation":"status"}
{"operation":"stop"}
```

Exec input records: `stdin` with `data`, `eof`, `resize` with `columns`/`rows`.
Output records: `started`, `stdout`/`stderr` with `data`, `exit` with `exitCode`,
or `error` with safe `message`. Network responses: `ready`, then `exit` when the
gateway stops. EOF on its control socket releases that route generation only.

Build: `swift build -c release --package-path native/workspace-runtime`.
Tests: `swift test --package-path native/workspace-runtime`.
VM execution requires `com.apple.security.virtualization` signing entitlement.

The repository gate uses `./scripts/build-workspace-runtime.sh --test` and keeps
Swift build products under `native/artifacts/workspace-runtime-build/swift`.
The script cleans compiled products when the checkout location changes while
retaining locked dependency checkouts. Subsequent runs build incrementally.

Full Xcode is needed for Swift Testing on this machine. Run tests with
`DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer xcrun swift test --package-path native/workspace-runtime`.
Set `ASURA_RUNTIME_TEST_ASSETS` to the signed runtime payload directory and
`ASURA_RUNTIME_TEST_GATEWAY` to the built Ethernet-enabled Go gateway to run
the fresh-VM integration test. It creates and removes only its own temporary
Alpine disk. It covers host-UID writable/read-only shares, concurrent exec, PTY
input/resizing, DNS/HTTP and a package-index download over 1 MiB through the host
gateway, blocked egress before/after a lease, and persistent restart.
`ASURA_RUNTIME_TEST_EXECUTABLE` optionally
selects a separately signed development binary.

The virtual NIC hardware ceiling is 1500 bytes because Apple requires it. The
SDK-configured guest interface and routed IP MTU remain 1280 bytes. A sidecar
lock coordinates runtime ownership without conflicting with VZ's disk lock.
VM stop errors return failure, so callers cannot promote an unflushed disk.
Both serve and network commands detect parent death every 250 ms. A route gets
SIGTERM first, followed by SIGKILL after one second if it does not stop. Socket
cleanup checks its device/inode identity before removing an endpoint.
