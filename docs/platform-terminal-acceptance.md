# Windows and Linux terminal acceptance

Windows and Linux packaging gates prove that managed code compiles, the packaged libghostty-vt
runtime is present, and deterministic terminal contracts pass. They do not prove interactive PTY,
Avalonia rendering, compositor, keyboard, IME, accessibility, clipboard, mouse, suspend, or
process-lifecycle behavior. A release candidate needs observations from named physical or
self-hosted interactive systems.

These checks exercise the unified desktop terminal described by
[ADR 0040](adr/0040-cross-platform-libghostty-vt-terminal.md): Porta.Pty byte transport,
libghostty-vt canonical state and protocol encoding, and an ordinary Avalonia-managed control.
They do not assume an XTerm.NET fallback or a platform-native terminal child view.

The named-host runner deliberately does not automate those observations. It fingerprints the exact
package, identifies libghostty-vt from the native-terminal component manifest and Porta.Pty from
`Asura.deps.json`, starts that package, presents one bounded checklist, sanitizes operator
notes, and rejects incomplete evidence. There is no `SKIP` state:

- `PASS` means the named operator performed and observed the check on that host and package;
- `FAIL` means the behavior was exercised and did not meet the check;
- `BLOCKED` means the check could not be performed or observed, including unavailable IME,
  suspend policy, headless/virtual display, redirected runner input, or an invalid desktop session.

Overall `PASS` requires all checks to pass. Any failure makes the run `FAIL`; otherwise any
unobserved check makes it `BLOCKED`. Exit codes are `0`, `1`, and `2` respectively (`64` is command
usage failure).

## Run on the target host

Use a stable system ID, operator ID, and release-candidate label containing only letters, digits,
periods, underscores, or hyphens. Do not use a generic system name such as `windows` or `linux`.

```powershell
pwsh ./scripts/platform-terminal-acceptance.ps1 `
  -Platform Windows `
  -SystemName win11-lab-01 `
  -Observer operator-01 `
  -BuildLabel rc-20260723-1 `
  -PackagePath C:\release\asura-win-x64
```

```powershell
pwsh ./scripts/platform-terminal-acceptance.ps1 `
  -Platform LinuxX11 `
  -SystemName ubuntu-x11-lab-01 `
  -Observer operator-02 `
  -BuildLabel rc-20260723-1 `
  -PackagePath /opt/candidates/asura-linux-x64
```

The PowerShell file is a thin launcher for the tested .NET runner. It can also be invoked directly:

```bash
./.dotnet/dotnet run --project tools/Asura.TerminalAcceptance -- run \
  --platform LinuxX11 \
  --system-name ubuntu-x11-lab-01 \
  --observer operator-02 \
  --build-label rc-20260723-1 \
  --package /opt/candidates/asura-linux-x64
```

Linux acceptance requires a real X11 session with `XDG_SESSION_TYPE=x11` and `DISPLAY`. The runner
blocks Wayland/XWayland, a virtual X server mapped to the active local display, a remote `DISPLAY`,
a container, an automation-environment marker, or redirected standard input/output before starting
the package. These probes are fail-closed evidence boundaries, not proof of physical hardware; the
first operator check remains mandatory. A remote-session marker is preserved as a warning; the
operator must use `BLOCKED` when remote input prevents direct keyboard, pointer, IME, compositor,
global-shortcut, or sleep testing.

## Required observation matrix

The catalog is versioned with the runner and records all of these checks in a fixed order:

1. named physical or self-hosted interactive desktop;
2. fingerprinted package, libghostty-vt/Avalonia/Porta.Pty backend, and real PTY;
3. interactive full-screen TUI with redraw and confirmation;
4. Unicode glyph fallback, combining/wide characters, selection, wrap, and cell fidelity;
5. real IME preedit, candidates, cancel/selection, committed text, and cursor alignment;
6. continuous resize and child-PTY grid synchronization;
7. mouse press/release/drag/wheel reporting plus ordinary selection when reporting is off;
8. clipboard copy/paste, guarded control-character paste, and fail-closed OSC 52 behavior;
9. alternate-screen entry, resize/redraw, and primary scrollback/cursor restoration;
10. OS-global Quick Terminal focus, toggle, restore, conflict, and Escape policy;
11. real host sleep/wake recovery (screen lock, process stop, and container pause do not count);
12. PTY/application lifecycle, active-work confirmation, repeated close, and process cleanup.

This matrix does not replace the separate named-host
[accessibility acceptance](platform-accessibility-acceptance.md). VoiceOver,
Narrator, and Orca must verify the same packaged Avalonia terminal's accessible
name, focus, caret and text exposure, announcements, scaling, and keyboard-only
operation before that platform is release-ready.

The final lifecycle check must close the package started by the runner. A claimed lifecycle `PASS`
is changed to `FAIL` if that parent process remains alive, and a valid overall pass records both the
operator observation and that runner check. The runner requests process-tree termination when a
candidate remains after observations and makes the same best-effort request on Ctrl+C. The parent
exit is verified; the operator's `ps`/Task Manager observation remains responsible for detecting an
already detached descendant. Cleanup is recorded separately and never turns a check into a pass.

The complete package and backend identity are fingerprinted again after cleanup. Any change or
inability to reproduce the original fingerprint fails the packaged-backend check, so one receipt
cannot silently span two package states.

## Evidence and sanitization

Each schema-v3 run writes a new directory beneath `artifacts/platform-acceptance` containing:

- `evidence.json`, the schema-versioned machine-readable record;
- `evidence.md`, the human-readable summary;
- `evidence.json.sha256`, a digest sidecar checked by the validator.

Build identity includes the operator-supplied label, executable SHA-256, and a deterministic package
manifest SHA-256 over every relative file name, length, and content digest. Backend identity records
the packaged libghostty-vt component identity, ordinary Avalonia managed presentation, Porta.Pty
version, and OS-specific PTY substrate. Evidence does not contain the absolute package path. The
identity proves what was packaged; the named-host checks prove how that package behaved.

Free-form notes are normalized and bounded. Common credentials and authorization values, URLs,
private-key material, email addresses, IPv4/IPv6 addresses, home and other absolute paths, and
control characters are redacted before writing; the redaction count is recorded. This is defense
in depth, not permission to paste secrets. Use synthetic clipboard and terminal text, and never
enter shell history, clipboard payloads, usernames, remote addresses, credentials, or absolute
paths.

Validate an archived run before citing it:

```bash
./.dotnet/dotnet run --project tools/Asura.TerminalAcceptance -- \
  validate artifacts/platform-acceptance/<run-directory>
```

Validation rejects duplicate or unknown JSON properties and checks the schema, exact matrix and
ordering, timestamps, result aggregation, host boundary, lifecycle/cleanup consistency, sanitized
notes, build/backend identity, SHA-256 sidecar, and the deterministic Markdown rendering. Archive
all three files with the exact package; do not describe an OS/backend as passing from an unfilled
checklist, unit test, screenshot alone, Xvfb run, or evidence whose digest does not validate. The
digest detects changes but is not a signature and does not authenticate the operator or host.

## Supplementary Linux arm64 Xvfb/Openbox run

The repository also includes a reproducible, bounded Docker/Xvfb run for the self-contained
`linux-arm64` package:

```bash
./scripts/linux-x11-packaged-acceptance.sh
```

It builds from a source copy inside `mcr.microsoft.com/dotnet/sdk:10.0`, starts the packaged Avalonia
desktop on a synthetic Xvfb display with Openbox, and drives the libghostty-vt state engine and
ordinary Avalonia terminal control through a real PTY.
It records package hashes, logs, screenshots, Unicode byte round-trip, resize propagation, an
interactive `less` session, SGR mouse reports, guarded paste behavior, OSC 52 policy, scoped X11
shortcut behavior, active-work cancellation, and process cleanup. Guarded-paste acceptance uses a
unique token and a later causal PTY barrier; close acceptance retains the exact child PID/start-time
identity; lifecycle cleanup checks captured descendant identities even after reparenting.

Every invocation creates a unique output directory and refuses an existing destination, so stale
artifacts cannot be merged into a new receipt. The receipt is published atomically with
`evidence.json` last. Setup failures also emit a deterministic infrastructure-failure receipt, and
declared evidence paths are validated before publication. The declared system name is
`docker-linux-arm64-xvfb-openbox`.

The latest local artifact is
`artifacts/platform-acceptance/20260723-renderer-focus-linux-arm64-xvfb`. Its result is
`NOT_PASSING`: ten bounded checks pass, seven checks remain `NOT_PROVEN`, and the synthetic Openbox
focus sequence fails to deliver the final lifecycle command after close cancellation and Quick
Terminal interaction. The package nevertheless exits with code 0 and no captured descendants. This
failure is retained rather than converted into a pass.

That retained artifact predates ADR 0040 and is historical evidence only. It is
not evidence for the current libghostty-vt package; a current candidate needs a
new receipt whose backend fingerprint names libghostty-vt and Porta.Pty.

This supplementary run deliberately exits nonzero while any check is failed or not proven. Xvfb
cannot prove IME preedit/candidate placement, glyph/cell fidelity without explicit visual review,
physical-host window-manager/compositor behavior, sleep/wake recovery, or Windows behavior. Its
evidence must not be relabeled as the named physical/self-hosted Linux acceptance required above.
