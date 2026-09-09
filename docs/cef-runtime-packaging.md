# CEF runtime packaging

Asura owns the bundled Chromium runtime as a native, security-sensitive
dependency. It does not restore the missing `runtime.<rid>.Exclr8Cef` packages
referenced by the upstream 0.8.0 NuGet package. A pinned Exclr8CEF source
snapshot is built with the reviewed Asura patch-set manifest instead.
`ASURA-SOURCE-SNAPSHOT.sha256` binds every vendored source file and rejects
missing, extra, linked, or changed paths; `ASURA-PATCHSET.sha256`
separately identifies the 19 reviewed differences from the pinned upstream
commit. Both manifest digests are mandatory catalog and receipt identities.

## Supported runtime identifiers

The reviewed catalog covers exactly:

| RID | CEF distribution | Deployment layout |
|---|---|---|
| `osx-arm64` | `macosarm64` | Framework and five helpers in `Contents/Frameworks` |
| `osx-x64` | `macosx64` | Framework and five helpers in `Contents/Frameworks` |
| `win-x64` | `windows64` | Flat beside `Asura.exe`, with `locales/` |
| `linux-x64` | `linux64` | Flat beside `Asura`, with `locales/` |
| `linux-arm64` | `linuxarm64` | Flat beside `Asura`, with `locales/` |

`win-arm64` is intentionally not in the Asura target matrix or the
binding's runtime graph.

The catalog's archive SHA-1 values and filenames are checked against CEF's
official `https://cef-builds.spotifycdn.com/index.json`; SHA-256 values are
computed over the exact downloaded archive bytes before extraction.

## Artifact contract

`scripts/build-cef-runtime.sh --rid <rid>` stages a private directory and then
publishes it under `native/artifacts/<rid>/cef`. Packaging consumes only this
explicit root; it never reaches into a mutable CMake build directory.

Every root retains:

- `CEF-LICENSE.txt`, the exact BSD license from the pinned CEF archive;
- `CEF-CREDITS.html`, Chromium's complete generated third-party credits;
- `EXCLR8CEF-LICENSE.txt`, the binding's MIT license;
- `cef-runtime-build-receipt.json`.

The receipt binds the reviewed catalog bytes, CEF version, RID/platform,
official archive SHA-1, locally computed archive SHA-256, upstream binding
commit, patched shim identity `0.8.0-asura.6`, complete vendored-source
snapshot SHA-256, Asura patch-set SHA-256, successful build result, and
every staged regular file's normalized path, length, SHA-256, and Unix mode
where applicable. Symlinks, special files,
unknown RIDs, incomplete locale/resource sets, missing helpers, mismatched
helper property lists, wrong-architecture PE/ELF/Mach-O binaries, or a changed
byte fail closed.

Create or validate a receipt directly with the packaging tool:

```sh
dotnet run --project tools/Asura.Packaging -- \
  cef-runtime-receipt \
  --runtime-root /tmp/staged-cef \
  --catalog licenses/cef-runtime-components.json \
  --runtime-identifier linux-x64 \
  --archive-sha1 <40-lowercase-hex> \
  --archive-sha256 <64-lowercase-hex> \
  --patch-set-sha256 <64-lowercase-hex> \
  --source-snapshot-sha256 <64-lowercase-hex> \
  --output /tmp/staged-cef/cef-runtime-build-receipt.json

dotnet run --project tools/Asura.Packaging -- \
  cef-runtime-validate \
  --runtime-root native/artifacts/linux-x64/cef \
  --catalog licenses/cef-runtime-components.json \
  --runtime-identifier linux-x64
```

## Platform closure

Windows and Linux require the shim, `libcef`, ICU data, both scale-factor
resource packs, the V8 context snapshot, EGL/GLES and SwiftShader/Vulkan
libraries and manifest, and at least `locales/en-US.pak`. Windows additionally
requires `chrome_elf.dll`, `d3dcompiler_47.dll`, and the x64 DXIL compiler
pair. Linux additionally requires an executable `chrome-sandbox`; installer
ownership/setuid or user-namespace sandbox acceptance remains a release gate
rather than something the build silently changes. The upstream NuGet staging
script's size-oriented Linux trimming is deliberately not reused for this
bundled runtime.

CEF 150's production Windows sandbox requires a native bootstrap that starts
the CLR after sandbox initialization. That launcher is not implemented in this
pass. Secure Windows builds must therefore fail closed; running without the
sandbox is not an acceptable release fallback.

macOS requires a materialized, link-free framework plus exactly these nested
bundles:

- `Asura Helper.app`;
- `Asura Helper (Alerts).app`;
- `Asura Helper (GPU).app`;
- `Asura Helper (Plugin).app`;
- `Asura Helper (Renderer).app`.

The main helper is passed to CEF as `browser_subprocess_path`; CEF derives the
process-specific variants from that name. Their executable names and bundle
identifiers are therefore validated, not treated as cosmetic metadata.

## Evidence and updates

macOS package assembly installs the three license/credits files, an exact copy
of the reviewed catalog and receipt, and `CEF-SBOM.spdx.json`. The SPDX document
records both CEF and Exclr8CEF, the upstream archive checksums, binding commit,
full source-snapshot and patch digests, licenses, dependency relationship, and
unresolved release blockers. The full byte closure remains in the adjacent
receipt rather than inflating the SPDX package list.

That receipt describes the verified, unsigned staging root. macOS assembly
rehashes every mapped copy before signing. Developer ID signing necessarily
changes Mach-O bytes and adds signature resources, so the existing package
acceptance fingerprint is generated after signing and notarization to bind the
final application bundle.

CEF is Chromium and must follow Chromium's security cadence. Updating it means
changing the pinned version and official archive checksums, rebuilding all five
RIDs, reviewing binding/API and patch drift, regenerating receipts, and running
browser smoke, sandbox, packaging, signing, and platform acceptance gates.
