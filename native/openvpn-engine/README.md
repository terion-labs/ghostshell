# Host-owned OpenVPN packet engine

This executable embeds pinned OpenVPN 3 Core using its external TUN factory.
The only packet interface is an inherited, connected Unix `SOCK_DGRAM` socket.
It does not create a host TUN device or change host routes, DNS, or proxy settings.
The Go gateway owns workspace attachment, split-route policy, DNS forwarding,
and the optional loopback SOCKS projection.

## Process contract

```
asura-openvpn-engine --config /private/profile.ovpn --tun-fd 3 --username USER
```

The parent supplies a password as one newline-terminated line on stdin, then
closes stdin. An empty line supports certificate-only profiles. Passwords never
appear in arguments, files, or diagnostics. The profile is a bounded regular
file opened without following symlinks; inline certificates and keys remain
private profile content.

After Core reports `CONNECTED`, stdout emits exactly one `READY v1 ` JSON line:

```json
{"ipv4_address":"10.8.0.2/24","ipv6_address":null,"mtu":1500,"dns_servers":["10.8.0.1"],"ipv4_routes":null,"ipv6_routes":[],"exclude_routes":[]}
```

Addresses include their assigned prefix. A null route list means a full tunnel
for that family; an empty list means no included destinations. Exclusions carry
CIDR prefixes of either family. DNS contains only negotiated plain port-53
resolvers; it may be empty. The parent supplies an explicit in-tunnel fallback
and always routes VPN DNS through the VPN, including in split configurations.

FD3 carries one raw IP packet per datagram with **no four-byte macOS utun prefix**.
The custom factory overrides Core's macOS prefix default. Core owns a duplicate
of FD3; the parent owns the opposite endpoint. A fatal error, disconnect, or
renegotiation requiring a new session terminates the engine. The parent must
discard that route and complete a new authenticated attachment before reuse.

TAP, external PKI, explicit encrypted DNS, and mandatory DNSSEC policies are
rejected because the parent contract cannot implement them faithfully. No shell
scripts or OS TUN adapter are used. Core logs are suppressed; terminal diagnostics
contain only fixed error categories, never exception or profile text.

## Build and tests

On Apple Silicon macOS, run `./scripts/build-openvpn-engine.sh`. It verifies the
pinned source archives, statically links all non-system dependencies, checks the
result with `otool`, runs the native contract tests, and stages licenses and a
checksum manifest with the executable. Versions and source hashes are recorded
in `VERSIONS.txt` and `THIRD-PARTY-NOTICES.txt`.

`./scripts/build-openvpn-engine.sh --test` rebuilds current first-party C++ and
runs CTest against the retained dependency cache without downloading anything.
`--verify` checks the staged artifact manifest. `--config FILE --validate`
evaluates a profile without connecting and emits only `VALID v1` or a fixed error.

The Go host tests exercise bounded readiness parsing, route semantics, forced
VPN DNS, credential privacy, raw datagram framing, child reaping, and startup
cancellation using a local fixture process. Live VPN tests require an explicit
test profile and credentials; deterministic tests do not contact public peers.
