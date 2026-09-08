# Managed dependency adapters

SharpCompress 0.50.3 and SSH.NET 2026.0.0 are restored from NuGet, with versions
in `Directory.Packages.props` and package hashes in the dependency lock files.
Their upstream source is not part of the application source tree. SqlClient
and CEF retain their existing source-build boundaries.

These are not entirely public-API integrations. Two deliberately narrow,
version-pinned compatibility boundaries replace the source forks. Package
upgrades must validate those boundaries rather than treating them as stable
upstream contracts. Neither adapter uses runtime code generation.

## ZIP directory traversal

`ZipDirectoryEnumeration` calls SharpCompress's protected `LoadEntries` method
through a single .NET `UnsafeAccessor`. Its public factory returns `ZipArchive`,
whose constructors are internal, so external inheritance cannot substitute an
overridden archive. Using the public `Entries` collection would retain every
skipped header during deep-page listing. A sequential local-header reader is
not equivalent for encrypted entries and data descriptors.

The adapter consumes the central-directory iterator within the archive's
lifetime. It must not be enumerated concurrently or replaced with a fallback
to `Entries`. `ArchivePagingTests` cover first/deep pages, collection of skipped
headers while the archive remains alive, encrypted metadata, ZIP64, long names,
and cancellation. They also reject accidentally loading the old public fork
method from stale build output.

## Authenticated SSH forwarding

`SshNetDirectTcpipChannel` isolates five calls: obtain the client's session,
create a direct-tcpip channel, open it, check whether it actually opened, and
bind its socket. Inaccessible reference types are named explicitly with .NET
10 `UnsafeAccessorType` attributes. An assembly-version guard rejects an
unreviewed version before credentials are loaded. Normal `IDisposable` and
`IForwardedPort` contracts handle lifetime; there is no general reflection
dispatch mechanism. The same session getter supplies the public concrete
`Session.Disconnected` event: graceful disconnects must close the listener,
not just failures reported through `SshClient.ErrorOccurred`.

`AuthenticatedSshSocksProxy` owns the application-side SOCKS5 listener and RFC
1929 authentication. It opens no SSH channel before authentication and a valid
CONNECT request, limits pending authentication, and applies a negotiation
deadline. There is no second unauthenticated SOCKS listener. Destination names
go to SSH without local DNS resolution, and server refusal cannot be reported
as a successful SOCKS connection. Shutdown closes the listener and active
sockets. SSH login, workspace proxy routing, credential ownership and verified
host-key route identity remain in `SshNetBrowserTunnelFactory`.

SSH.NET's public dynamic-forward listener cannot be extended at its private
authentication/channel boundary. Tmds.Ssh offers public destination streams,
but the evaluated 0.24.0 API cannot publicly inject our workspace route into
the SSH server connection. OpenSSH `-W` would require a larger credential and
host-key integration, with different connection-sharing behavior on Windows.
Neither was adopted as an apparently equivalent drop-in replacement.

## Upgrade checks

1. Review the exact upstream implementation behind each accessor; do not simply
   change a version guard to make an upgrade pass.
2. Update the centrally pinned version and regenerate ordinary, Windows,
   per-runtime, and macOS Native AOT lock files.
3. Run the archive and SSH socket regression suites, plus an actual local SSH
   forwarding round trip. A mocked protocol test alone does not prove the
   internal channel calls still work.
4. Publish and execute the adapters with Native AOT as well as the normal JIT
   runtime. Signature resolution and trimming must both remain valid.
5. Refresh package provenance, notices and the owner-approved dependency record,
   then run `./scripts/check.sh --full`.

If an upstream public extension point becomes available, remove the
corresponding accessor instead of growing this compatibility layer.
