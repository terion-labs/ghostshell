# macOS accessibility acceptance probe

This probe performs a passive, bounded inspection of one running Asura main window through the macOS Accessibility API. It provides repeatable implementation evidence without collecting terminal contents or other user text.

## Safety contract

- The probe never requests the macOS Accessibility permission prompt. It returns `BLOCKED` when the invoking process is not already trusted.
- It returns `BLOCKED` before making Accessibility calls when the current GUI session is locked, or when lock state cannot be established.
- It proceeds only when exactly one running `Asura.app` package exposes exactly one window whose accessibility title is exactly `Asura`. The package must declare bundle identifier `sh.asura`, name the executable `Asura`, and run that executable directly from `Contents/MacOS`; a `dotnet Asura.dll` development process is intentionally rejected.
- A non-blocked receipt binds the inspected process to its positive PID and lowercase SHA-256 executable digest. Paths and arguments are never emitted. The digest identifies the exact executable bytes inspected; it is not a substitute for future release-signature or notarization evidence.
- It walks only child structure plus role, title/description, help, focus, enabled, selected, and expanded metadata. AX messaging and the walk have fixed timeouts, and the walk has fixed node, depth, and child-count bounds with cycle detection.
- Discovery is independently bounded to 256 running-application records and 16 windows per verified Asura package. Package identity is checked before creating a per-application AX object, so unrelated applications' AX trees are never queried. Every verified window-title read rechecks the discovery deadline. Application, window, or deadline excess returns a validator-enforced `BLOCKED` receipt with aggregate counts only.
- It never reads `AXValue`, selected text, terminal buffers, window images, screen pixels, file paths, process arguments, or clipboard data.
- Raw titles, descriptions, and help strings are never placed in the receipt. The receipt contains counts and fixed result codes only.
- Named-control coverage includes buttons and fields plus common browser, list, table, grid, tab, menu, row, cell, outline, radio-group, and toolbar roles.
- Fixed terminal names count as terminals only when exposed by the explicit `AXGroup`, `AXScrollArea`, or `AXTextArea` role allowlist. A fixed terminal name on another role is a failure rather than terminal evidence.
- Version 1.2 is deliberately passive and executes no Accessibility actions or keyboard shortcuts. Its `actionsExecuted` list is therefore always empty.

## Run

Build and launch a packaged Asura application satisfying the package identity contract above so that one main window is open, unlock the session, and grant Accessibility access to the terminal or automation host that will invoke the probe. Then run:

```bash
./scripts/acceptance/mac-accessibility/run.sh > /tmp/asura-mac-accessibility.json
probe_status=$?
python3 ./scripts/acceptance/mac-accessibility/validate_receipt.py \
  /tmp/asura-mac-accessibility.json
```

Exit status `0` means `PASS`, `1` means `FAIL`, and `2` means `BLOCKED`. The runner validates the receipt before printing it. A malformed or privacy-weakened receipt is rejected rather than accepted as evidence.

Run the deterministic validator tests and compile the Swift probe with warnings treated as errors:

```bash
./scripts/acceptance/mac-accessibility/test.sh
```

The test also enforces the Swift source's AX-attribute allowlist, named-control and terminal-role sets, packaged-build identity checks, application/window limits, per-window deadline guard, actual-focus requirement, rejects action/content APIs, and requires all attribute reads to remain centralized at one reviewable boundary.

## What this proves

A `PASS` proves that the receipt is bound to one packaged executable PID and digest, the inspected window's bounded accessibility tree was readable, audited interactive/collection roles had non-empty title/description metadata, at least one element actually reported focused, and any recognized Asura terminal element had its fixed accessible name on a plausible terminal role. It also records whether the current surface contained a recognized terminal.

This probe does **not** prove VoiceOver announcements, rotor placement, pronunciation, speech interruption, logical focus order, visible focus quality, keyboard-only workflow completion, text scaling/reflow, or clipping. Those remain manual acceptance work on the named macOS host. Windows Narrator and Linux Orca require their own platform acceptance.
