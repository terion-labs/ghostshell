# ADR 0053: Persistent workspace execution isolation

**Status:** Accepted
**Date:** 2026-08-31

## Context

A workspace needs an execution boundary that can later own one proxy, VPN, or
tailnet independently of the host and of other workspaces. Local terminal
processes must cross that boundary too. The environment must be light enough to
keep several workspaces available, and packages installed inside it must remain
after the last window releases it.

An execution boundary is not automatically a filesystem-security boundary.
Every configured read/write host mount gives the guest the same authority over
that host data as Asura has. Mounting the host home or root deliberately
gives up confidentiality for those paths.

## Decision

`WorkspaceDefinition` stores an isolation intent and an ordered collection of
host-to-guest mounts. Each mount has an absolute host source, an absolute Linux
guest destination, and explicit read-only/read-write access. The CLI bootstrap
currently accepts directory sources; the durable model does not prevent a native
provider from supporting regular files later. An empty collection is valid and
creates a guest-only workspace. Asura never adds a host mount implicitly;
every source and access level comes from an explicit workspace setting.

The isolation setting and mounts are restart configuration. The editor keeps
them editable while a workspace is open. Saving a changed configuration asks
for confirmation, closes the workspace's processes, applies the saved
configuration, and reopens the workspace. A process-wide occupancy registry
reserves the exact window/runtime source before open or recovery performs
asynchronous work. Configuration saves acquire the opposing lease after the
local runtime closes, so opens and saves cannot cross in different windows.
Recovery stores the captured isolation intent and resolved mounts and refuses to
restore across a changed execution boundary. Tabs and panels can move only
between runtime workspaces that share the same provider resource.

Isolation preparation and the isolated process-launch adapter fail closed. A
missing provider, unsupported connection kind, unavailable credential bridge,
unmapped working directory, stale configuration, or failed recovery at that
boundary never substitutes a host process. The workspace tile shows the active
runtime binding while a workspace is open and durable intent while it is closed.

One provider resource is named deterministically from the durable workspace ID.
Every window acquires a distinct lease. Releasing the last lease stops compute
but does not delete the writable guest root filesystem, so installed packages
persist. Ownership metadata and the cold specification are validated before a
resource is reused.

### macOS

The provider is an app-bundled, signed Swift helper over
[Apple Containerization](https://github.com/apple/containerization/blob/main/README.md).
Containerization already runs each Linux container in its own lightweight VM,
uses Virtualization.framework on Apple silicon, provides OCI image and ext4
storage management, exposes host directories through virtiofs, and controls
guest processes through `vminitd` over vsock. Building on bare
`VZVirtualMachine` would make Asura recreate all of those layers without
producing a lighter execution boundary.

As of 2026-09-05, the default provider uses Containerization 0.42.0 directly.
One signed `workspace-runtime` process owns each VM and persistent ext4 disk;
mount changes replace VM configuration without exporting or rebuilding that disk.
Its only virtual NIC terminates at the host-owned gateway, and its private Unix
control channel supports structured exec, PTY resizing and route leases. The
runtime, kernel, init filesystem and required libraries are bundled. No installed
Apple CLI or custom guest network helper is required. SDK workspaces start fresh;
CLI workspace migration is explicitly out of scope. See
[the implementation contract](../architecture/host-owned-workspace-network.md).

### Earlier CLI bootstrap (superseded)

The first provider was a bootstrap adapter over Apple's installed
[`container` CLI](https://github.com/apple/container/blob/main/docs/command-reference.md).
It is available only on Apple silicon with macOS 26 and a compatible external
runtime. It creates one persistent named VM-backed container per workspace and
uses structured process arguments rather than shell command strings. Apple's CLI
accepts mounts only when it creates a container. To change them, the adapter
stops the workspace container, exports its complete writable root, builds a
workspace-owned OCI image from that export, deletes only the old container
definition, and recreates it with the requested mounts. Export and image build
have cancellation but no wall-clock timeout because their duration depends on
the workspace filesystem size. Installed packages and guest-only files survive
the replacement. The native helper should eventually keep the workspace data
disk separate from replaceable VM configuration so mount and future network
changes avoid this export and rebuild cost.

The helper boundary will expose versioned acquire/release, inspect, PTY exec,
reset/delete, and network-policy operations. XPC is preferred for the packaged
macOS app. SSH-agent forwarding must become an explicit workspace capability;
the bootstrap adapter does not forward the host agent implicitly.

Verified SSH fails closed in the bootstrap adapter. A host-side key scan would
escape the workspace's future network boundary, while delegating `accept-new` to
guest OpenSSH would create a second trust decision that Asura never showed
or approved. Until the provider can scan through the isolated network, return
the candidate for app review, and atomically persist the approved key inside the
guest, isolated SSH is limited to profiles where the user explicitly selected
`InsecureIgnore`. `/root` remains provider-owned so future private trust storage
cannot be shadowed by a host mount. Preparing a local workspace never downloads
OpenSSH; the bootstrap launch installs the optional guest client lazily, through
the guest network, only when an explicitly unverified SSH terminal is requested.

### Linux and Windows

Raw Firecracker is not the default Linux direction. It requires Asura to
assemble the kernel, root disk, guest agent, TAP/NAT, and a separate host-file
sharing mechanism. Apple Containerization now ships a Linux backend using
cloud-hypervisor, KVM, virtiofsd, TAP, and the same `vminitd` contract. We will
prototype that shared backend first; Linux remains unavailable until Asura
packages its host dependencies and its unprivileged KVM access, networking,
lifecycle, and recovery meet the same contract as macOS.

Plain per-workspace WSL distributions are also insufficient. Microsoft documents
that WSL 2 distributions share one utility VM and network namespace. Windows
therefore needs either a managed WSL environment with a separate container and
network namespace per workspace, or a Hyper-V VM per workspace when the stronger
kernel boundary is required.

### Adapter coverage and networking seam

This increment routes local terminals and explicitly verification-disabled,
brokerless SSH terminal launches through the macOS bootstrap provider. Verified
SSH, Docker and WSL terminal kinds, host credential brokers, and connection
diagnostics fail closed at that terminal boundary. Non-terminal browser, file,
database, Git, Docker, process, statistics, and agent clients still require
isolate-scoped adapters. The product setting remains **Isolate workspace**; an
adapter's implementation coverage does not redefine the user's workspace-level
intent. Hosts without a packaged provider cannot enable isolation, while a
previously isolated portable definition can still be switched off there for
recovery.

The application contract reserves a workspace-network attachment capability but
the bootstrap provider does not advertise it. A later workspace networking layer
will attach proxy, VPN, or Tailscale policy to this resource. Without isolation,
the terminal runtime may instead receive proxy environment variables; this ADR
does not implement either networking path.

## Consequences

Workspace isolation now has a durable, provider-neutral model, configurable host
mounts, captured runtime identity, recovery semantics, and lifecycle ownership.
It can grow into a whole-workspace network boundary without changing the saved
workspace identity or terminal planning seam.

The initial macOS path depends on Apple's separately installed CLI rather than
the final app-owned backend. Linux and Windows intentionally fail as unavailable.
Full workspace isolation requires moving every networked session host behind the
provider abstraction, adding a native macOS helper,
building equivalent Linux and Windows providers, and supplying an explicit reset
and delete flow for persistent runtime configuration. Until that flow exists,
deleting an isolated workspace definition explicitly warns that its platform
environment and installed packages remain for manual platform-runtime cleanup.
