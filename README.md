# ▵ GhostSHELL

A ghost in your shell. GhostSHELL is a native terminal workspace with an in-process AI agent that operates local and remote sessions. One window holds terminals, an embedded Chromium browser, files, databases, Redis, Docker, Git, and system monitors. The agent is plain .NET running inside the desktop process. There is no Node.js sidecar on your machine and nothing to install on remote hosts.

GhostSHELL is free and open-source software under the [MIT license](./LICENSE).

Website: [ghostshell.terion.name](https://ghostshell.terion.name). Early alpha. A macOS Apple-silicon build ships from [Releases](https://github.com/terion-labs/ghostshel/releases/latest); other platforms build from source.

## How the agent is kept on a leash

The agent core is provider-neutral. Profiles cover Anthropic, OpenAI, Google, xAI, DeepSeek, Moonshot AI, OpenRouter, GitHub Copilot, Amazon Bedrock, Ollama, and custom OpenAI-compatible endpoints. Credentials and OAuth sessions live in the OS vault; the app passes opaque references around. A protocol without a production adapter refuses to run rather than degrading quietly.

The agent is scoped to one workspace. It carries the full tool set in context and reads which panels exist and which tools apply to them; the runtime still revalidates every action against the current workspace state. Model tool calls start inert; nothing executes until the runtime and session host authorize one typed action. You approve each mutation individually, or grant an explicit, time-bounded run-only window for terminal actions. Typing in the terminal yourself revokes the agent's input lease immediately.

Terminal, browser, file, database, Docker, process, statistics, workspace-graph, web-search, and MCP operations all pass through the same session-host boundary: capability checks, policy, cancellation, and a durable audit trail. MCP servers connect over stdio or Streamable HTTP with frozen run manifests. The agent drives the browser semantically: it captures an accessibility snapshot of the page, then clicks, fills, or checks elements through opaque references from that snapshot, never guessed coordinates. A stale reference from an outdated snapshot is rejected. Page content is treated as untrusted data, and browser mutations pass through the same approval flow as everything else.

## What works today

Early alpha, but the core loop is real:

- Workspaces hold their own tabs, connections, and saved multi-panel layouts, and everything survives a restart. Recent-session history stores metadata only, never terminal contents.
- Optional workspace isolation: flip a switch and the whole workspace runs in its own persistent Linux VM (Apple Containerization on macOS), with explicit host mounts and its own network namespace. Installed packages survive across sessions; the host stays clean. See [ADR 0053](./docs/adr/0053-persistent-workspace-execution-isolation.md).
- Connection work has an owned, UI-free backend. Isolated workspaces use their VM; custom-network and SSH-routed connections use a private service VM on supported macOS builds. Non-isolated Direct connections use host children. The Linux backend downloads on demand, outside the app archive. See [ADR 0056](./docs/adr/0056-workspace-database-backend.md) for routing, resource limits and protocol boundaries.
- Panels for the daily set: terminal, embedded Chromium browser, files, databases, Redis, Docker, Git, process monitor, and live system statistics.
- The docked agent streams its reasoning and token usage, takes images where the provider supports them, searches the web, and accepts steering and queued follow-ups mid-run. You approve each action, and you can cancel at any point.
- The agent types into the same terminal you do. The moment you touch the keyboard, it stops.
- The browser is CEF rendered off-screen straight into the UI. Permission prompts fail closed, and a crashed renderer gets replaced without taking the app down.
- One terminal engine (libghostty-vt) and one input path on macOS, Windows, and Linux: IME, mouse, resize, clipboard, the lot. No per-platform terminal forks.

Everything above sits under deterministic test suites, from domain logic down to architecture contracts that fail the build when a boundary is crossed.

## Stack

- [.NET 10 LTS](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [Avalonia 12](https://docs.avaloniaui.net/docs/get-started/)
- [libghostty-vt](https://github.com/ghostty-org/ghostty) through an isolated C ABI for canonical terminal state and protocol encoding
- [Porta.Pty](https://github.com/IvanJosipovic/Porta.Pty) for PTY process transport on every supported desktop OS

libghostty-vt's API is not stable yet, so GhostSHELL pins Ghostty commit `08f039fbb3dea9c6b1cdb5ff4550666598122346` and applies a narrow, reviewed patch overlay in a disposable checkout. Every Ghostty C declaration stays private to `GhostShell.Terminal`. The overlay exposes normalized OSC 133 lifecycle events, canonical virtual Kitty placement geometry, and Ghostty-backed full-scrollback search. It also enables Ghostty's Wuffs PNG decoder and publishes an exact extension-ABI marker. Application-owned render DTOs carry damage, live cursor state, underline variants and colors, and Kitty image placement into the Avalonia control. No Ghostty or Porta.Pty type crosses into Core, Protocol, SessionHost, or App.

The terminal is not a native child view. All three OSes share the same Avalonia presentation and input path; there is no embedded `NSView`, no `NativeControlHost`, no IOSurface handoff. [ADR 0040](./docs/adr/0040-cross-platform-libghostty-vt-terminal.md) explains why.

## Build and run

Install the workspace-local .NET SDK, then build the pinned native runtimes for the current host:

```sh
GHOSTSHELL_SKIP_NATIVE=1 ./scripts/bootstrap.sh
./scripts/build-libghostty-vt.sh
./scripts/build-cef-runtime.sh --rid osx-arm64
```

The first native build downloads the pinned Zig toolchain and Ghostty source, so expect several minutes. It applies the reviewed overlay to a disposable checkout, runs Ghostty's patched VT tests, verifies every managed import plus the exact GhostSHELL extension ABI, and publishes the library with its export manifest, license, and build receipt under `native/artifacts/<rid>`. It also fetches JetBrains Mono 2.304 as declared by the Ghostty pin, verifies all four faces and the OFL by exact hash, and publishes them under `native/artifacts/common`.

Then build, test, and run:

```sh
./scripts/check.sh --full
./.dotnet/dotnet run --project src/GhostShell.Desktop/GhostShell.Desktop.csproj
```

On macOS, `dotnet run` first assembles the framework-dependent build into a private development `.app` under `src/GhostShell.Desktop/obj`. CEF needs its `Contents/Frameworks` layout, and this preserves it without weakening the native-payload checks that release packaging relies on.

To exercise the real PTY and libghostty-vt pipeline, split UTF-8 input, render damage, cursor and underline state, semantic shell events, PTY flush, and process exit:

```sh
./.dotnet/dotnet test tests/GhostShell.Terminal.Tests/GhostShell.Terminal.Tests.csproj
```

## The check gate

The repository gate is deterministic and warning-free on purpose. It uses the exact SDK from `global.json`, locked NuGet restores with central package versions and vulnerability auditing, `dotnet format`, all compiler and security analyzers, architecture contract tests, and the test projects in a stable sequence. `./scripts/check.sh --quick` runs the restore, audit, formatting, build, and architecture parts for fast iteration. Bootstrap installs the checked-in pre-commit and pre-push hooks; `./scripts/install-hooks.sh` reinstalls them.

Day-to-day verification is local: the pre-commit and pre-push hooks run the gate before anything leaves your machine. A `v<major>.<minor>.<patch>` tag has a stronger pre-push gate: [`scripts/rehearse-macos-release.sh`](scripts/rehearse-macos-release.sh) must first build the read-only sealed source with the same native toolchains, produce a Developer-ID signed and notarized Velopack application, prove that its portable archive and full update package contain the same app, verify the extracted archive with Gatekeeper, and revalidate its exact security evidence. The rehearsal needs full Xcode 26+, GraalVM 25.0.4, LLVM lld 22, and the same six `APPLE_*` release environment variables consumed by GitHub Actions. It imports the certificate and notary key into an ephemeral keychain and deletes that keychain on exit. Docker cannot reproduce this boundary because Xcode, Developer ID signing, notarization, stapling, and Gatekeeper require macOS; the script instead uses disposable source, dependency, build, signing, and evidence roots and deletes them after verification.

Only after that local receipt exists does GitHub Actions run for the tag, entirely on Apple-silicon macOS runners. It builds the verified native runtime first, runs the managed suite as six parallel sections (core, agent, app, services, data-browser, terminal-host) next to a complete Release build and a format-and-boundaries job, then repeats Velopack assembly, signing, notarization, stapling, Gatekeeper validation, and exact evidence assembly before publication. The release contains the stable `GhostShell-macOS-arm64.zip` bootstrap archive and checksum plus the `osx-arm64-stable` full package, feed, and checksums used by in-app updates. The [archive](https://github.com/terion-labs/ghostshell/releases/latest/download/GhostShell-macOS-arm64.zip) and [checksum](https://github.com/terion-labs/ghostshell/releases/latest/download/GhostShell-macOS-arm64.zip.sha256) retain permanent URLs. The release fails closed if legal clearance, exact package/feed validation, or release credentials are absent. A separate path-filtered workflow runs the database-viewer integration suite on pull requests that touch it.

Updates are user initiated. GhostSHELL never contacts GitHub in the background. A direct GitHub installation can check from About, download the exact Velopack package, and restart into it after a graceful shutdown. Store and package-manager installations defer to their platform channel, development builds do not update, and direct bundles under `/Applications` remain download/apply-disabled until the privileged replacement boundary is separately closed. The GitHub Releases ZIP remains the initial-install and manual-recovery path.

### Run the local macOS release control build

Run [`scripts/rehearse-macos-release.sh`](./scripts/rehearse-macos-release.sh) before pushing any release tag. This is the control build. It runs `./scripts/check.sh --full`, builds all native payloads from the tagged source, creates the signed Velopack release, notarizes and staples it, checks it with Gatekeeper, and validates the release evidence. [`scripts/package-macos-github-release.sh`](./scripts/package-macos-github-release.sh) alone is not a substitute because its credential-free mode produces an ad-hoc local package.

The rehearsal requires an Apple Silicon Mac, full Xcode 26 or newer, GraalVM Native Image 25.0.4, and LLVM `ld64.lld` 22.x. Keep the local credentials in the ignored `.apple/` directory and their shell assignments in the ignored `.env` file. Both paths are already covered by `.gitignore`; never force-add them.

Use one assignment per variable. `.env` is a Bash fragment sourced from the repository root, so derive the transport encodings from the credential files instead of pasting a second copy of either key:

```bash
APPLE_CERTIFICATE_P12_BASE64="$(/usr/bin/base64 -i .apple/developer-id-application.p12 | tr -d '\n')"
APPLE_CERTIFICATE_PASSWORD='replace-with-the-p12-export-password'
APPLE_DEVELOPER_ID_APPLICATION='Developer ID Application: Example Name (TEAMID)'
APPLE_NOTARY_ISSUER_ID='replace-with-app-store-connect-issuer-id'
APPLE_NOTARY_KEY_ID='replace-with-app-store-connect-key-id'
APPLE_NOTARY_PRIVATE_KEY_BASE64="$(/usr/bin/base64 -i .apple/AuthKey_KEYID.p8 | tr -d '\n')"

GRAALVM_HOME='/absolute/path/to/graalvm-25.0.4'
GHOSTSHELL_XCODE_APP='/Applications/Xcode.app'
GHOSTSHELL_NATIVE_AOT_LINKER='/opt/homebrew/opt/lld@22/bin/ld64.lld'
```

The two Apple keys are not interchangeable. `APPLE_CERTIFICATE_P12_BASE64` must decode to a password-protected PKCS#12 file containing the Developer ID Application certificate and its matching private key. A `.cer` file and its CSR contain no signing private key and are not enough. `APPLE_NOTARY_PRIVATE_KEY_BASE64` must decode to the App Store Connect API `.p8` key used by `notarytool`. Base64 only transports these files; it does not encrypt them.

Do not regenerate, unpack, print, or repack an existing signing identity just to run the build. Do not enable shell tracing with `set -x` while loading `.env`. If the current `.p12` imports with its existing password, keep it. The rehearsal imports it into a temporary keychain, confirms that `APPLE_DEVELOPER_ID_APPLICATION` is present, and removes the keychain on exit.

Commit the exact release source, create an annotated tag at `HEAD`, load the environment without echoing it, and run the rehearsal:

```bash
release_tag='v<major>.<minor>.<patch>' # Replace this with the next unused version.

git status --short # This must print nothing.
git tag -a "$release_tag" -m "GhostShell ${release_tag#v}"

set -a
source ./.env
set +a

./scripts/rehearse-macos-release.sh --tag "$release_tag"
```

The script rejects a dirty tracked tree, a tag that does not resolve to `HEAD`, the wrong toolchain versions, missing credentials, an unusable signing identity, and failed notarization. A pass writes a receipt under `.git/ghostshell-release-rehearsals/` bound to the tag, commit, and tree. Any source change requires a new commit, a new tag or corrected unpublished tag, and another rehearsal. Never move a tag that has already been pushed.

After the rehearsal passes, push the commit and that exact tag. The pre-push hook reuses only a receipt that still matches all three identities; GitHub Actions then rebuilds and publishes from the tag.

```bash
git push --atomic origin main "$release_tag"
```

## Updating dependencies

A dependency bump has to refresh every lock graph that CI and release packaging read: the ordinary and Windows-targeted managed graphs, each reviewed desktop RID, and the macOS Native AOT graph. Regenerate them deliberately, review the lock-file diffs, then run the full gate:

```sh
./.dotnet/dotnet restore GhostShell.slnx --force-evaluate
./.dotnet/dotnet restore GhostShell.slnx \
  -p:GhostShellWindowsBuild=true --force-evaluate
./.dotnet/dotnet restore src/GhostShell.Desktop/GhostShell.Desktop.csproj \
  --runtime linux-x64 --force-evaluate
./.dotnet/dotnet restore src/GhostShell.Desktop/GhostShell.Desktop.csproj \
  --runtime linux-arm64 --force-evaluate
./.dotnet/dotnet restore src/GhostShell.Desktop/GhostShell.Desktop.csproj \
  --runtime osx-x64 --force-evaluate
./.dotnet/dotnet restore src/GhostShell.Desktop/GhostShell.Desktop.csproj \
  --runtime osx-arm64 --force-evaluate
./.dotnet/dotnet restore src/GhostShell.Desktop/GhostShell.Desktop.csproj \
  --runtime osx-arm64 --force-evaluate \
  -p:GhostShellMacReleaseNativeAot=true
./.dotnet/dotnet restore src/GhostShell.Desktop/GhostShell.Desktop.csproj \
  --runtime win-x64 --force-evaluate
./scripts/check.sh --full
```

## Provider sign-in notes

GitHub Copilot device authorization uses GitHub's public first-party Copilot client identity by default. A distribution with its own registered GitHub OAuth app can override it before launching the desktop process:

```sh
export GHOSTSHELL_GITHUB_OAUTH_CLIENT_ID="your-ghostshell-oauth-app-client-id"
```

OpenAI browser and device authorization use OpenAI's public Codex client identity and need no variable. Browser login owns the registered `http://localhost:1455/auth/callback` listener for the duration of the flow; if another process holds that port, login fails closed. GitHub's long-lived device token stays in the vault as refresh material only. GhostSHELL exchanges it locally for a bounded Copilot API token before any provider traffic and repeats the exchange when that token expires.

## macOS packaging

To build a non-launching, ad-hoc sealed macOS arm64 Native AOT bundle candidate, install LLVM's `ld64.lld` first (or point `GHOSTSHELL_NATIVE_AOT_LINKER` at it):

```sh
mkdir -p artifacts/macos-arm64-rc
./scripts/package-macos.sh \
  --version 0.1.0 \
  --build-version 1 \
  --output artifacts/macos-arm64-rc/GhostShell.app
```

To exercise the complete direct-distribution format locally, including
`UpdateMac`, `sq.version`, the portable archive, full package, channel feed, and
cross-artifact validation:

```sh
./scripts/package-macos-github-release.sh \
  --version 0.1.0 \
  --build-version 1 \
  --output-dir artifacts/macos-arm64-direct
```

The packager emits a speed-optimized Native AOT executable with no managed DLLs, `.deps.json`, runtime configuration, or JIT runtime in the bundle. A separate locked self-contained publish exists only to validate the reviewed dependency catalog and produce license evidence. The packager refuses incomplete native payloads, dependency-catalog drift, tampered NuGet archives, and existing destinations. It validates the published `libghostty-vt.dylib` against its export manifest, build receipt, and license, checks the JetBrains Mono quartet and OFL, emits deterministic SPDX 2.3 evidence for the managed dependency closure, and verifies the exact bundle identity. The [macOS packaging guide](./docs/macos-packaging.md) documents the guarantees and the remaining signing, notarization, and named-host gates.

## Platform acceptance

Packaged interactive-TUI rendering, physical input, IME, clipboard, mouse, resize, sleep/wake, and PTY lifecycle must produce passing evidence on named host systems before a release claims them. [`scripts/platform-terminal-acceptance.ps1`](./scripts/platform-terminal-acceptance.ps1) and the [platform acceptance guide](./docs/platform-terminal-acceptance.md) capture that evidence per host and package; an unrun checklist never counts as a pass.

Physical keyboard and screen-reader acceptance works the same way. [`scripts/platform-accessibility-acceptance.ps1`](./scripts/platform-accessibility-acceptance.ps1) with the [VoiceOver, Narrator, and Orca guide](./docs/platform-accessibility-acceptance.md) binds observations to one package and one reader identity and emits a sanitized receipt.

A few platform specifics worth knowing:

- OS-global Quick Terminal shortcuts use Carbon on macOS, `RegisterHotKey` on Windows, and `XGrabKey` under real X11. Wayland reports as unsupported until a compositor-global portal backend can be built and verified safely.
- A paste containing escape sequences stops in an explicit in-terminal confirmation prompt. OSC 8 `http`/`https` links open through a revalidated confirmation. Process-originated OSC 52 clipboard access without a broker fails closed.

## Solution map

| Project | What it holds |
| --- | --- |
| `src/GhostShell.Core` | framework-independent IDs, definitions, invariants, and state machines |
| `src/GhostShell.Application` | typed application and session operations, lifecycle, capability, attachment, input-lease, and engine ports |
| `src/GhostShell.Protocol` | versioned transport envelopes and stream contracts |
| `src/GhostShell.Agent` | the provider-neutral conversation loop: strict stream reduction, inert tool batches, steering, cancellation fencing, compaction, checkpoints |
| `src/GhostShell.Agent.Providers` | native provider adapters, API-key and OAuth resolution, device/browser authorization, HTTP/SSE parsing, model discovery |
| `src/GhostShell.Agent.Runtime` | workspace-scoped provider and tool orchestration; the full tool registry stays in context and actions validate against live panels |
| `src/GhostShell.Mcp` | stdio and Streamable HTTP MCP sessions with bounded discovery and frozen run manifests |
| `src/GhostShell.SessionHost` | the in-process runtime registry: ordered events, revisions, attachments, leases, browser action guards, close policy |
| `src/GhostShell.Terminal` | the libghostty-vt state/input adapter and Porta.Pty transport behind render, automation, input, and lifecycle ports |
| `src/GhostShell.Browser` | the CEF engine runtime and per-workspace browser profiles, including SSH-routed network contexts |
| `src/GhostShell.Files` | file providers (local, SFTP, FTP, S3, WebDAV, SMB), transfer sessions, and the SSH tunnel factories |
| `src/GhostShell.Databases` | the database panel client and SQL dialects for the supported engines |
| `src/GhostShell.ConnectionBackend` | bounded SQL, Redis, file and HTTP IPC, owned worker lifetimes and host credential callbacks |
| `src/GhostShell.Backend` | UI-free on-demand Linux backend entry point |
| `src/GhostShell.Redis` | Redis panel sessions |
| `src/GhostShell.Docker` | the Docker engine client for local and remote daemons |
| `src/GhostShell.Git` | the Git panel over a CLI adapter |
| `src/GhostShell.Docking` | panel docking and layout |
| `src/GhostShell.Monitoring` | package-free local resource sampling behind privacy-bounded statistics and process ports |
| `src/GhostShell.Previews` | file content previews |
| `src/GhostShell.Infrastructure` | encrypted persistence, OS vaults, startup protection, and platform adapters |
| `src/GhostShell.App` | Avalonia presentation; depends only on application ports and Core projections |
| `src/GhostShell.Desktop` | the executable composition root and platform registrations |
| `tools/GhostShell.Packaging` | fail-closed release-candidate bundle assembly |
| `native/ghostty-vt` | the reviewed patch overlay and notices for the pinned libghostty-vt build |
| `website/` | the marketing site (Nuxt, deployed to GitHub Pages by `.github/workflows/website.yml`) |
| `tests/*` | domain, application, protocol, terminal, session-host, provider, persistence, desktop, and architecture suites |

[architecture.md](./docs/architecture.md) documents the boundaries and the implementation sequence.
