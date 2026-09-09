# VoiceOver, Narrator, and Orca acceptance

Automated tests prove Asura's accessibility metadata, semantic text resources, focus policies,
and host-preference mapping. The passive macOS AX probe proves a bounded tree can be read without
collecting user content. Neither proves what a person actually hears or whether a complete workflow
works with a screen reader and keyboard on a real desktop.

The named-host M1 runner records those physical observations against one exact packaged build. It
supports only these mappings:

- macOS with VoiceOver;
- Windows with Narrator;
- Linux X11 with Orca.

There is no `SKIP` or substitute-reader state. `PASS` means every assertion was performed by the
named operator with the expected screen reader. `FAIL` means an exercised assertion was wrong.
`BLOCKED` means it could not be established, including an unavailable reader, remote session,
redirected runner, CI/container, virtual X server, Wayland/XWayland, missing production text-scale
path, or incomplete restoration. Overall `PASS` requires every assertion to pass. Exit codes are
`0` for `PASS`, `1` for `FAIL`, `2` for `BLOCKED`, and `64` for command usage errors.

## Prepare the named host

Use a dedicated test account or profile containing only synthetic definitions, paths, host names,
terminal text, and clipboard data. Connect a real keyboard, display, and audio output directly to
the host. The runner blocks common detected automation, container, remote-session,
redirected-terminal, and virtual-display markers. No cross-platform software probe can enumerate
every remote-control tool, so the first required operator assertion confirms direct local keyboard,
display, and audio use. On Linux, use a real local X11 session with Orca and its queried AT-SPI
desktop bus; Xvfb, VNC, Xpra, XWayland, and forwarded `DISPLAY` sessions are not acceptance
substitutes.

Start the expected screen reader before the runner. The runner verifies one platform-specific
identity before launching Asura and verifies the same identity again afterward:

- VoiceOver is the running system application with bundle identifier `com.apple.VoiceOver`;
- Narrator is the running Windows `System32` executable;
- Orca is one running `orca` process whose live `/proc` executable and command line bind it to the
  system `/usr/bin/orca` launcher or its system Python interpreter, the launcher reports a readable
  version, and `org.a11y.Bus.GetAddress` returns the active AT-SPI bus address.

The runner never starts, stops, or kills a screen reader. It also never changes host accessibility
preferences. The operator makes the required preference changes through normal production settings
and restores every changed setting before closing the package.

## Run

Use stable system, observer, and release-candidate identifiers containing only letters, digits,
periods, underscores, or hyphens. A generic system name such as `windows`, `linux`, or `localhost`
is rejected.

```powershell
pwsh ./scripts/platform-accessibility-acceptance.ps1 `
  -Platform Windows `
  -ScreenReader Narrator `
  -SystemName win11-a11y-lab-01 `
  -Observer operator-01 `
  -BuildLabel rc-20260723-1 `
  -PackagePath C:\release\asura-win-x64
```

```powershell
pwsh ./scripts/platform-accessibility-acceptance.ps1 `
  -Platform LinuxX11 `
  -ScreenReader Orca `
  -SystemName ubuntu-x11-a11y-01 `
  -Observer operator-02 `
  -BuildLabel rc-20260723-1 `
  -PackagePath /opt/candidates/asura-linux-x64
```

```powershell
pwsh ./scripts/platform-accessibility-acceptance.ps1 `
  -Platform MacOS `
  -ScreenReader VoiceOver `
  -SystemName mac-a11y-lab-01 `
  -Observer operator-03 `
  -BuildLabel rc-20260723-1 `
  -PackagePath /Applications/Asura.app
```

The PowerShell file is a thin launcher. The tested .NET runner can also be invoked directly:

```bash
./.dotnet/dotnet run --project tools/Asura.AccessibilityAcceptance -- run \
  --platform MacOS \
  --screen-reader VoiceOver \
  --system-name mac-a11y-lab-01 \
  --observer operator-03 \
  --build-label rc-20260723-1 \
  --package /Applications/Asura.app
```

The runner fingerprints the package before launch, starts its exact executable, and fingerprints
the complete package again after cleanup. The manifest includes the package root, every directory,
every regular file, deterministic entry kinds and paths, platform-relevant attributes or Unix mode
bits, file lengths, and file-content hashes. Empty-directory and permission changes therefore alter
the manifest. macOS requires a directory named `Asura.app` whose XML or binary property list
declares bundle identifier `sh.asura` and executable `Asura`. Windows and Linux require
`Asura.exe` and `Asura` respectively. Symbolic links/reparse points, FIFOs, sockets,
devices, and packages outside the bounded entry/file/byte/depth limits are rejected before content
reads. The evidence output directory is canonicalized and must resolve outside the package so
publishing the receipt cannot mutate the build that was just fingerprinted.

## Fixed observation matrix

Runner 1.1/catalog 1.1 uses the schema-v1 evidence shape and contains twelve checks in a fixed order, each with fixed assertion IDs:

1. named local, unlocked, direct interactive host with a synthetic profile;
2. expected screen reader identity, speech output, and native reader controls;
3. exact package identity, successful launch, and unchanged post-run fingerprint;
4. application/window orientation and deterministic initial focus;
5. representative control names, roles, states, and values;
6. screen-reader order, forward/reverse Tab order, visible focus, and no dead ends;
7. launcher, workspace/tab/panel, settings, palette, and chooser workflows without pointer input;
8. modal containment, safe Escape/cancel, exact focus return, and keyboard layout editing;
9. legitimate high text scale, reflow, clipping, contrast, and non-color status;
10. live text-scale/motion/transparency behavior and reduced-effects Quick Terminal;
11. session/connection/error/recovery announcements without focus theft;
12. terminal semantics, terminal focus escape, Quick Terminal focus restore, preference restoration,
    normal package exit, and continued screen-reader operation.

Each assertion is entered separately as `PASS`, `FAIL`, or `BLOCKED`; one free-form `PASS` cannot
stand in for the matrix. Notes must summarize synthetic behavior and must not quote a speech stream.
The runner independently downgrades lifecycle claims when the package parent remains alive, the exit
is unsuccessful, a captured descendant remains live, descendant sampling fails, the package
changes, or the expected screen-reader identity cannot be reverified. From package launch until
lifecycle judgment, a bounded background sampler retains stable PID/start identities every 50 ms;
Windows cleanup uses retained process handles, Linux uses identity-bound `pidfd` signaling, and
macOS refuses an unsafe PID-only signal and instead requires manual cleanup. A final `PASS` also
requires the operator to confirm that every changed accessibility preference was restored.
Runner-requested process-tree termination is cleanup, never lifecycle acceptance. Sampling is not
OS-level containment and cannot prove absence of a process that fully detaches between samples.

macOS exposes reduced-motion and reduced-transparency preferences to Asura but no host-wide
application text-scale factor. For the high-text-scale observation, use the production
**Settings > Appearance > Application text size** control, save `200%` or `250%`, verify the live
reflow in every open Asura window, and restore `Follow host` before completing the run. The
stored override replaces the unavailable host factor; it does not multiply it. Display
magnification is still not text reflow and must not be reinterpreted as a pass.

## Evidence boundary and validation

Every run reserves a new exclusive directory beneath `artifacts/accessibility-acceptance`. It never
merges with an existing run. A complete directory contains exactly:

- `evidence.json`, strict machine-readable schema-v1 evidence;
- `evidence.md`, a deterministic human-readable rendering;
- `evidence.json.sha256`, the JSON digest sidecar.

JSON is published last as the completion marker. Validation rejects duplicate or unknown JSON
properties, catalog or assertion drift, wrong platform/screen-reader mapping, unsafe notes,
inconsistent aggregation, missing lifecycle/restoration evidence, digest mismatch, Markdown drift,
and extra files. Validation enumerates only the bounded directory prefix needed to establish the
three-file contract, requires regular non-link files, and rejects any evidence file over 1 MB before
reading its content.

```bash
./.dotnet/dotnet run --project tools/Asura.AccessibilityAcceptance -- \
  validate artifacts/accessibility-acceptance/<run-directory>
```

The evidence contains fixed assertion results, sanitized summary notes, package hashes, bounded host
metadata, a one-way truncated host-name fingerprint, and screen-reader product/version identity.
The runner never collects screenshots, audio, speech transcripts, raw AX/UIA/AT-SPI trees, terminal
content, clipboard payloads, or environment dumps. Operators must not enter usernames, addresses,
credentials, or paths in notes; recognized secret, address, URL, and absolute-path forms are
redacted and the validator rejects their unsanitized forms. This is defense in depth, not a general
secret classifier. The host fingerprint supports receipt correlation but is not an anonymity
guarantee for a guessable machine name. The digest detects changes but is not a signature and does
not authenticate the operator or host.

The existing macOS AX probe remains complementary structural evidence. Archive its validated receipt
alongside a macOS named-host run when available, but do not relabel that passive probe as VoiceOver
acceptance and do not use it as a substitute for Narrator or Orca observations.
