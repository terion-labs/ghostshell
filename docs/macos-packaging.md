# macOS release-candidate packaging

Asura can currently produce a Native AOT macOS arm64 application
bundle for local release validation. Candidates are completely ad-hoc sealed by
default; the
same pipeline can apply nested Developer ID signatures and submit
the finished application for notarization when release credentials are
provided. Its
terminal runtime is the same managed-presentation pipeline used on Windows and
Linux: Porta.Pty transports raw process bytes, libghostty-vt owns canonical
terminal state and protocol encoding, and an ordinary Avalonia control renders
the terminal. The package does not contain the retired AppKit terminal shim or
full libghostty renderer.

## Prerequisites

Install the workspace-local .NET SDK and build the pinned native terminal and
SQL-language runtimes:

```sh
ASURA_SKIP_NATIVE=1 ./scripts/bootstrap.sh
./scripts/build-libghostty-vt.sh --rid osx-arm64
./scripts/build-sql-language-worker.sh --local --rid osx-arm64
./scripts/build-cef-runtime.sh --rid osx-arm64
```

Install LLVM's `ld64.lld` as well. Asura's Native AOT object exceeds the
limits of Apple's current linker. Set `ASURA_NATIVE_AOT_LINKER` when the
linker is not on `PATH`.

Install full Xcode 26 or newer before packaging. CommandLineTools alone does not
contain `actool` and the release script fails before publishing when full Xcode
is unavailable. Apple's separate Icon Composer app is needed only to regenerate
the checked-in compatibility icon:

```sh
./scripts/build-macos-icon.sh
```

`assets/macos/Asura.icon` is the layered source. It uses automatic and
system fills so Icon Composer can render default, dark, tinted, and clear
appearances while macOS supplies its mask, material, shadow, and highlight.
`Asura.icns` is the deterministic compatibility rendition for macOS 13
through 25. During every package build, `compile-macos-app-icon.sh` requires
Xcode `actool` 26 or newer and compiles the same layered source into
`Assets.car`. The script verifies the generated partial property list and uses
`assetutil` to require a named `Asura` icon image. Copying a raw `.icon`
document into an application bundle is not a supported substitute.

On macOS 26, the running application recolors its Dock icon from the current
system accent. That runtime rendition keeps the artwork inside the same
optically inset 1024-pixel canvas used by Apple's production grid; the outer
transparent margin is intentional and must not be cropped when changing the
runtime SVG. Both sources also apply the same upward optical correction to the
triangular mark, whose broad base makes geometric centering look low.

`assets/macos/product-identity.json` is the reviewed macOS identity contract.
It records the canonical display name, executable, bundle identifier, icon
name, first-party ownership and license, maintainer approval, the six required
appearances, and exact SHA-256 hashes for the Icon Composer document, source
SVG, and ICNS fallback. Package assembly re-hashes each input and rejects an
unknown field, missing appearance, changed source, incomplete ICNS size set, or
identity disagreement. The exact manifest is retained under
`Contents/Resources/Licenses/ProductIdentity`.

The native build checks out Ghostty commit
`08f039fbb3dea9c6b1cdb5ff4550666598122346`, applies the ordered patch overlay
from `native/ghostty-vt/patches` to a disposable checkout, builds the public
libghostty-vt C ABI with pinned Zig 0.16.0, runs Ghostty's patched
`test-lib-vt` suite, verifies the complete managed-import export set and exact
Asura extension ABI, and publishes:

- `native/artifacts/osx-arm64/libghostty-vt.dylib`;
- `GHOSTTY-LICENSE`;
- `ghostty-vt-required-exports.txt`;
- `native-terminal-build-receipt.json`;
- a manifest plus the reviewed Bash, Fish, and Zsh shell-integration resources
  copied byte-for-byte from that Ghostty commit.

The same successful build also verifies and atomically publishes the official
JetBrains Mono 2.304 regular, bold, italic, and bold-italic faces under
`native/artifacts/common/fonts/JetBrainsMono`. Zig fetches the exact URL and
package hash declared by the pinned Ghostty source. A separate reviewed
catalog, sorted manifest, OFL, and build receipt bind every font byte; Ghostty's
larger test-font resources are not packaged.

The CEF build consumes the exact release and Exclr8CEF source commit in
`licenses/cef-runtime-components.json`, verifies the already-patched vendored
source against the full-source and patch-set manifests, and uses disposable
build and staging trees to produce an explicit runtime root. The root
contains the CEF framework, Exclr8CEF shim, all five `Asura Helper` app
variants, CEF license and Chromium credits, binding license, and a sorted
file-level SHA-256 receipt. The packager validates that receipt before running
the more expensive desktop publish.

The build receipt binds the component catalog, target RID, source commit,
toolchain archive, target/build options, passing patched-test result, patch-set
digest, extension ABI, reviewed export-manifest digest, library digest, license
digest, and shell-integration manifest. A missing or dirty pinned source
checkout, patch or test failure, missing export, incompatible extension ABI,
hash mismatch, linked resource, unexpected Mach-O install name, or incomplete
output fails the native build before publication.

The SQL-language build separately compiles Calcite with GraalVM Native Image,
runs the linked executable's framed-protocol smoke test, and atomically publishes
`asura-sql-language`, its resolved dependency list, third-party notices,
and `build-receipt.json`. The receipt binds the executable hash, `osx-arm64`
RID, `darwin-arm64` ABI, protocol version, minimum macOS version, legal-closure
format, dependency/document/review-required counts, and hashes of both legal
files.

## Build a candidate

Create the destination parent first, then supply explicit product and build
versions:

```sh
mkdir -p artifacts/macos-arm64-rc
./scripts/package-macos.sh \
  --version 0.1.0 \
  --build-version 1 \
  --runtime-identifier osx-arm64 \
  --output artifacts/macos-arm64-rc/Asura.app
```

The destination must be named `Asura.app` and must not already exist. The
script publishes an `osx-arm64`, speed-optimized Native AOT application, rejects symbolic
links and special entries, verifies that the executable and libghostty-vt are
arm64 Mach-O files, verifies the CEF shim, framework libraries,
and five helper executables against that same architecture, and requires the
terminal library install name
`@rpath/libghostty-vt.dylib`. The dylib may depend only on macOS `libSystem` and
may export only the Ghostty C ABI.

The SQL-language worker is also mandatory. Packaging verifies its RID, ABI,
artifact name, protocol version, file type, and SHA-256 against the build
receipt. It then reads the Mach-O `LC_BUILD_VERSION` command, requires its
minimum OS version to match the receipt, and rejects any minimum newer than
macOS 13. The verified receipt must survive both publish passes and the
final bundle byte-for-byte. Packaging also re-hashes the dependency manifest
and third-party notices before publish, after publish, and in the assembled
bundle; all three copies must match the receipt.

The isolated workspace backend is a separate on-demand download, never Linux
code embedded in the macOS app. `./scripts/build-workspace-backend.sh` uses the
repository-pinned SDK and locked Linux ARM64 dependency graph to publish the
UI-free `Asura.Backend` with self-contained .NET 10.0.11. It rejects browser
and Avalonia assemblies, non-ARM64 native libraries, links, and unsafe paths.
Its deterministic `Asura-workspace-backend-arm64.tar.gz` includes a
`MANIFEST.sha256` for verifying extracted files; the signed app contains only
`Contents/Resources/runtimes/linux-arm64/workspace-backend/backend-assets.json`,
which pins the archive hash, length, and executable. Rehearsal builds this
sidecar locally, packaging rejects stale build receipts, and the release uploads
it beside the boot archive. Development uses the same descriptor and sets
`ASURA_WORKSPACE_BACKEND_ARCHIVE` to the local sidecar. The development
launcher rebuilds a stale backend before starting the app.

The project owner approved the recorded macOS dependencies and Linux ARM64/x64
backend distributions for Asura 0.1.41 in `asura-90w9`. Each platform retains
its own decision and exact input hashes in the matching
`licenses/*release-legal.json` record. Signed release assembly, rehearsal, and
the tag lane verify those records before publication; changes to the reviewed
inputs require a renewed decision.
The backends retain runtime, application, managed-package and SqlClient notices,
plus SMBLibrary license/source/replacement records. Their `.deps.json` files
identify the actual backend subsets; inherited desktop notices also describe
components that are not in these headless payloads. Owner acceptance is based
on the documented engineering evidence without independent legal review.

The temporary Native AOT executable's `LC_BUILD_VERSION` SDK field is updated to macOS 26.0
before package fingerprinting and is ad-hoc signed so the candidate remains
launchable. This opts native window chrome into the current macOS appearance;
it neither introduces a native terminal view nor raises the app's macOS 13
minimum system version. Release signing replaces this temporary signature.

The package assembly is non-destructive. It stages into a private sibling,
validates the complete candidate, then publishes with a no-overwrite directory
move. A failure removes the staging directory and leaves the requested
destination absent.

Adaptive icon compilation also fails closed before the managed publish. The
selected developer directory must be full Xcode, `actool` must report version
26 or newer, and both the generated partial plist and `Assets.car` must declare
`Asura` as the primary icon. The focused identity and release jobs run on
a macOS 26 host, select a matching Xcode installation explicitly, and repeat
the `assetutil` check after extracting the signed archive. This avoids the
Xcode 26 AssetRuntime crash observed when the hosted compiler ran on macOS 15.

The default CEF root is `native/artifacts/<rid>/cef`. A separately staged,
verified root can be supplied with `--cef-runtime-root`; it must still match
the checked-in catalog and its own receipt.

Standalone CEF runtime build, receipt, and catalog validation continue to
support `osx-x64`. Full Intel application packaging fails fast in this pass:
it requires a separate exact managed-component catalog and a verified x64
libghostty-vt payload/receipt before the app packager may claim that RID.

For a lower-level release candidate, use a Developer ID Application identity. The signing
script signs all Native AOT payload Mach-O files, CEF leaf libraries, the
framework, shared shims, five helper apps, and the outer app in nested-code
order without `codesign --deep` mutation:

```sh
./scripts/package-macos.sh \
  --version 0.1.0 \
  --build-version 1 \
  --runtime-identifier osx-arm64 \
  --output artifacts/macos-arm64-rc/Asura.app \
  --sign-identity "Developer ID Application: Example Corp (TEAMID)"
```

Add `--notary-profile asura-release` to ZIP the candidate temporarily,
submit it with `notarytool --wait`, staple the accepted ticket, and validate it
with both `stapler` and Gatekeeper. The profile must already exist in the
login keychain; credentials are never accepted on the command line or written
to package evidence.

The GitHub release lane must use the higher-level assembler, not publish the
lower-level candidate directly:

```sh
./scripts/package-macos-github-release.sh \
  --version 0.1.0 \
  --build-version 1 \
  --output-dir artifacts/macos-arm64-direct \
  --sign-identity "Developer ID Application: Example Corp (TEAMID)" \
  --notary-profile asura-release \
  --keychain /path/to/release.keychain-db \
  --release-evidence-dir /outside/source/release-evidence \
  --source-seal /outside/source/source-seal \
  --security-campaign-tool /outside/source/Asura.SecurityCampaign.dll \
  --build-artifacts-root /outside/source/build
```

The base packager signs nested code without notarizing it. Pinned Velopack then
adds `UpdateMac` and `sq.version`, signs the updater and final outer bundle,
notarizes and staples that final app, and derives the portable ZIP and full
update package. The release validator compares every packaged app byte with the
portable app, verifies the feed's exact package digest and size, and rejects any
symbolic link except `Contents/MacOS/sq.version` pointing to
`../Resources/sq.version`. Velopack 1.2 applies macOS update files with mode 755,
so the cross-artifact proof compares paths and bytes while the extracted
portable app retains the stricter mode-sensitive release fingerprint.

The tag workflow requires all six release secrets and fails before assembly if
any are absent:

- `APPLE_CERTIFICATE_P12_BASE64`;
- `APPLE_CERTIFICATE_PASSWORD`;
- `APPLE_DEVELOPER_ID_APPLICATION`;
- `APPLE_NOTARY_ISSUER_ID`;
- `APPLE_NOTARY_KEY_ID`;
- `APPLE_NOTARY_PRIVATE_KEY_BASE64`.

The certificate and App Store Connect private key are installed only in an
ephemeral runner keychain, which is deleted after the release steps. The final
ZIP is extracted and checked with strict `codesign` verification and Gatekeeper
before GitHub Release publication.

## Update and rollback policy

The macOS arm64 bundle now records `github-release`, `velopack`, and
`osx-arm64-stable` in `Contents/Resources/distribution.json`. Asura never
checks in the background. A user can check and download from the About page only
when Velopack confirms that the running bundle contains its installation
metadata and updater. The application contains no release-signing,
notarization, or GitHub credential.

Archives released before this lane do not contain Velopack metadata and cannot
update themselves. Install one update-aware ZIP manually; subsequent direct
releases can be checked, downloaded, and applied from About when the bundle is
in a user-writable location. The ZIP and checksum remain available for initial
installation and recovery. macOS independently verifies the Developer ID
signature, stapled notarization ticket, and Gatekeeper policy.

Keep the previous application bundle until the new version has launched and
opened the existing profile. Replacing the bundle does not move the profile
database, vault items, browser profiles, or cache. Restoring the previous
bundle is safe before the new version first opens the profile. After a newer
version migrates durable data, downgrade is supported only when that release's
notes explicitly declare schema compatibility or the user restores a
pre-update backup/export. The application never claims that an older binary
can read a newer schema.

macOS arm64 is the only current release package. Windows, Linux, Intel macOS,
and platform stores have no update channel until their packaging milestones are
implemented and tested.

## Validated payload

The packager fails closed unless the Native AOT publish contains:

- the Asura Native AOT executable, with no managed application DLLs,
  dependency manifest, runtime configuration, or JIT runtime;
- `Contents/Resources/Assets.car`, compiled by Xcode 26 or newer from the
  checked-in layered `assets/macos/Asura.icon` source and declared by
  `CFBundleIconName`;
- `Contents/Resources/Asura.icns`, containing every required 16 through
  1024 pixel compatibility rendition and declared by `CFBundleIconFile`;
- the exact approved product-identity manifest under
  `Contents/Resources/Licenses/ProductIdentity`;
- exactly the current terminal library `libghostty-vt.dylib` rather than
  `libasura-ghostty.dylib` or full `libghostty.dylib`;
- the pinned Ghostty license, native component catalog, and native build
  receipt;
- the exact reviewed libghostty-vt export manifest;
- the exact shell-integration manifest, notice, and reviewed Bash/Fish/Zsh
  files;
- the exact four-face JetBrains Mono closure, manifest, font catalog, build
  receipt, and retained OFL text;
- the reviewed managed-component catalog, NuGet archive evidence, .NET license,
  and third-party notices.
- the exact CEF 150 framework/resource/locale closure and Exclr8CEF shim;
- `Asura Helper.app` plus the Alerts, GPU, Plugin, and Renderer variants,
  each with matching executable and bundle identity;
- the CEF BSD license, Chromium `CREDITS.html`, Exclr8CEF MIT license, reviewed
  CEF catalog, file-level build receipt, and a deterministic CEF SPDX document.

Native provenance validation requires a passing patched-test receipt and the
exact extension ABI, then compares the packaged dylib, reviewed export
manifest, license, shell-integration manifest, and every staged shell resource
with the build receipt and native component catalog. A separate locked
self-contained publish provides managed provenance without entering the app
bundle. Validation compares its complete `Asura.deps.json` closure with the reviewed catalog, NuGet
content hashes, archive SHA-512 receipts, nuspec identity/version/license
metadata, and resolved dependency graph. Unknown, missing, malformed,
ambiguous, linked, or tampered evidence fails assembly.
First-party project identities, the SPDX document name, and its namespace use
the catalog's `${productVersion}` token, which the packager binds to the exact
release version. Vendored project and NuGet versions remain fixed and exact.

Terminal-font provenance separately checks the Ghostty dependency declaration,
official JetBrains Mono 2.304 Zig package hash, four exact TTF sizes and hashes,
TrueType headers, sorted manifest closure, exact catalog/receipt copies, and an
installed OFL copy identical to the packaged one. An extra face or file is a
package error, not an implicit extension point.

The bundle declares:

- display and bundle name `Asura`;
- bundle identifier `sh.asura`;
- executable `Contents/MacOS/Asura`;
- primary adaptive icon name `Asura` and compatibility icon file
  `Asura.icns`;
- minimum system version macOS 13;
- the supplied `CFBundleShortVersionString` and `CFBundleVersion`;
- `Contents/Resources/Licenses/GHOSTTY-LICENSE`;
- exact native catalog/receipt copies under
  `Contents/Resources/Licenses/Native`;
- `Contents/Resources/Licenses/JetBrainsMono-OFL.txt` plus exact font
  catalog/receipt copies under `Contents/Resources/Licenses/Native`;
- deterministic managed dependency and third-party evidence under
  `Contents/Resources/Licenses`.
- `Contents/Frameworks/Chromium Embedded Framework.framework` and the five
  CEF subprocess helper app bundles;
- `libexclr8cef.dylib` in both `Contents/MacOS` for the managed host and
  `Contents/Frameworks` for helper-process `@rpath` resolution.

The shell-integration runtime assets and their manifest live under
`Contents/Resources/ghostty/shell-integration`, outside the executable-only
code directory. The public terminal library remains beside the executable at
`Contents/MacOS/libghostty-vt.dylib`.
The inspectable font closure remains under
`Contents/Resources/fonts/JetBrainsMono`; Avalonia consumes the same verified
faces through its embedded-font collection.
The SQL language worker remains executable code under
`Contents/MacOS/runtimes/osx-arm64/native`; its receipt and legal metadata live
under `Contents/Resources/Native/SqlLanguage`.

## Inspect without launching

The package can be inspected without starting an application process:

```sh
./.dotnet/dotnet run \
  --project tools/Asura.AccessibilityAcceptance/Asura.AccessibilityAcceptance.csproj \
  --configuration Release \
  -- \
  inspect-package \
  --platform MacOS \
  --build-label macos-0.1.0-1 \
  --package artifacts/macos-arm64-rc/Asura.app
```

This checks bundle identity, executable mode, regular-file boundaries,
file-count and size limits, and the complete package manifest. It prints package
and executable digests but does not launch the candidate.

## Outstanding release work

`licenses/macos-release-legal.json` is the single macOS legal-closure decision.
It hashes the MIT project license, SMBLibrary source and replacement materials,
managed catalog, Ghostty and shell evidence, Inter font catalog, CEF catalog,
and SQL worker legal maps. Local packaging validates and carries either state
without changing it. The tag workflow runs the stricter
`macos-release-legal --require-clearance` command before signing, and refuses publication unless
`legalClearance` is true, `releaseBlockers` is empty, and the review fields are
complete. It also requires explicit `accepted-by-owner-for-macos` dispositions
for the managed catalog, native terminal and shell resources, CEF, and SQL
worker. The record hashes each nested evidence set, so an owner decision applies
only to those exact bytes. Windows and Linux are explicitly outside this record.

The project owner accepted the documented macOS distribution risks without an
independent legal review. `review.status` and `review.basis` state that decision
directly; `legalClearance` means internal release clearance, not a legal opinion
from counsel.

Asura is MIT licensed. SMBLibrary remains LGPL-3.0-or-later and is compiled
into the Native AOT executable. The bundle retains LGPLv3 and GPLv3 text, exact
source provenance for upstream commit
`255339717ccc9a278579d563f42939d9f2668506`, and
`SMBLIBRARY-SOURCE-AND-RELINKING.md`. Those materials explain how to replace the
library and rebuild Asura. The project owner accepts that documented path
for the exact macOS closure.

The pipeline supports Developer ID signing, Chromium JIT hardened-runtime
entitlements, notarization, and stapling, but possession of credentials does
not itself make a distributable release. The owner has made the license
decision. DMG or PKG creation, update-feed policy, and release-identity
operations remain separate gates. An unsigned or ad-hoc local candidate must
not be represented as notarized.

Adaptive icon structure is now a package gate, but visual acceptance remains a
release-host check. Inspect the exact signed candidate in Finder and the Dock
under default, dark, tinted, and clear modes on macOS 26 before claiming those
appearances. The automated `actool` and `assetutil` checks prove compilation and
identity, not the pixels selected by the live desktop.

The native component catalog deliberately reports `BLOCKED`: the exact linked
libghostty-vt and staged shell-integration source/license closure has not
completed an independent release review. In particular, the retained Bash/Zsh
upstream notices include GPL-covered portions. The macOS legal record hashes
that conservative catalog and records the project owner's explicit acceptance
of the exact closure without rewriting the underlying evidence snapshot.

The current macOS terminal library links only Apple's system
`/usr/lib/libSystem.B.dylib`. The prior gettext/libintl concern belongs to the
retired renderer pipeline and is not part of this `libghostty-vt` payload. The
SQL worker records its 48 runtime dependencies and one review exception in its
build receipt. CEF retains its exact macOS archive, BSD license, Chromium
credits, binding source snapshot, patches, and SPDX receipt. These facts close
the mechanical inventory questions. The legal record documents the owner's
acceptance of the remaining provenance uncertainty for macOS only.

Finally, a structurally valid bundle is not evidence that its interactive
terminal is ready to ship. Named-host rendering, full-screen TUI, physical
keyboard, IME, clipboard, mouse, resize, sleep/wake, PTY lifecycle, and
VoiceOver accessibility verification remain release gates for the exact
packaged candidate.
