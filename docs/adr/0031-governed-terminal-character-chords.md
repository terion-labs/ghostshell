# ADR 0031: Governed terminal character chords

- Status: Accepted
- Date: 2026-07-24
- Extends:
  [ADR 0013](0013-windows-linux-terminal-state-and-pty.md),
  [ADR 0019](0019-one-action-agent-capability-broker.md)
- Terminal-engine update:
  [ADR 0040](0040-cross-platform-libghostty-vt-terminal.md) supersedes the
  platform-split encoding and renderer details in this record. The closed
  chord, authorization, commit, and audit decisions remain accepted.
- Security basis:
  [Agent-to-tool threat model](../security/agent-tool-threat-model.md)

## Context

An agent that can operate an interactive terminal must express common
character chords such as `Ctrl+D`, `Ctrl+R`, `Ctrl+Z`, `Ctrl+L`, and
`Alt+X`. The existing `terminal.send_keys` contract deliberately covers only
a closed set of navigation and function keys. `terminal.send_text` and paste
are the wrong abstractions for terminal control characters, while accepting
arbitrary bytes, escape sequences, key codes, or modifier arrays would give
model data a much broader and platform-dependent input surface.

The same terminal engine on every desktop must preserve human-preemption and
irreversible-input semantics. Adding a Node/Pi sidecar would duplicate terminal
ownership and is outside the in-process desktop architecture.

## Decision

Asura adds one closed governed tool, `terminal.send_chord`, backed by a
typed `TerminalCharacterChord` application contract.

### Closed model input

The first contract accepts:

- exactly one lowercase ASCII character from `a` through `z`;
- exactly one modifier: `control` or `alt`.

Uppercase characters are rejected rather than normalized. Shift, Meta,
Control+Alt, modifier-free text, digits, punctuation, whitespace, Unicode,
multiple runes, raw controls, bytes, escape sequences, key codes, and
modifier arrays are unrepresentable.

An exact panel/session schema contains only `character` and `modifier`. A
broader Workspace schema or internal `OpenTab`/selected-terminal schema
additionally requires one host-generated `panel_id` from the current eligible-panel enum. The parser
independently enforces the same closed shape and never falls back to text,
paste, interrupt, or `terminal.send_keys`.

The trusted composer binds the exact session and canonical human-readable
chord, such as `Ctrl+D` or `Alt+X`, into approval material and the versioned
argument digest. The uppercase display convention does not imply Shift.

### Capability, risk, and authorization

The tool requires both `terminal.send_chord` and
`terminal.agent_input_barrier`. It is cataloged under
`DestructiveTerminalActions` with `Destructive` risk because chords can send
EOF, suspend a job, discard terminal state, or invoke application commands.

`Auto` escalates before execution. SessionHost independently accepts only an
exact `HumanApproval` or an explicitly confirmed run-local `YoloPolicy`
authorization and rejects `AutoPolicy`. Durable policy and recovery do not
carry YOLO.

SessionHost consumes one exact authorization, acquires one one-action agent
input lease, rechecks the chord capability and input barrier adjacent to
dispatch, and calls the typed automation port once. Human input preempts the
lease. There is no separate ungoverned application operation or public
session-host chord method.

### Engine encoding and commit boundary

The shared libghostty-vt engine validates the closed letter/modifier pair and
performs terminal-mode encoding from its live keyboard state. In legacy
keyboard mode, the initial contract has these canonical results:

| Chord | PTY bytes |
|---|---|
| `Ctrl+D` | `04` |
| `Ctrl+R` | `12` |
| `Ctrl+Z` | `1A` |
| `Ctrl+L` | `0C` |
| `Alt+X` | `1B 78` |

An empty generated sequence fails before PTY input. Managed code captures the
current physical-input authority before encoding and rechecks it immediately
before the ordered PTY write. A stale authority commits nothing. Caller or
lease cancellation before the successful PTY write commits no bytes;
successful `WriteAsync` is the irreversible boundary. Later cancellation or
flush failure preserves the committed receipt while failing the session
separately, so an already-written chord is never presented as safely retryable.
Shutdown settles every uncommitted acknowledgement.

Programmatic chords bypass Avalonia text composition and active keyboard-layout
translation while leaving legacy and Kitty keyboard-mode encoding to
libghostty-vt. They neither invoke the human-input path nor advance its
authority. No raw-byte, platform key-synthesis, or text-injection fallback
exists.

### Outcome and audit

A successful typed dispatch returns only a fixed completion receipt. Completion
audit uncertainty retries only the same immutable audit event, never the chord.
Provider continuation remains blocked until the one action has a confirmed
outcome.

Integration tests use a raw, no-echo terminal reader so Control+D and Control+Z
are observed as bytes rather than interpreted as EOF or job control. Engine
tests prove current-authority mappings, stale-authority refusal, invalid-input
rejection, physical-input separation, legacy encoding, and Kitty
disambiguate-mode encoding without host-layout reinterpretation.

## Consequences

- The agent can operate common interactive shell and TUI control chords without
  arbitrary terminal-byte authority.
- libghostty-vt owns mode-specific encoding on every desktop while the
  application retains one authorization and commit contract.
- The contract is independent of Avalonia and desktop chrome, so a future
  authenticated headless or ACP surface can reuse it without being implemented
  in this slice.
- Chord combinations, non-letter keys, and broader keyboard automation require
  a separate decision and threat review.

## Alternatives rejected

- Extending `terminal.send_keys` with printable letters would blur special-key
  and character-input semantics and make destructive chords look routine.
- Sending control characters through text or paste would bypass the explicit
  chord risk and approval material.
- Accepting arbitrary bytes or escape sequences would let model data bypass
  terminal-mode encoding and greatly widen authority.
- Launching a Node/Pi process would duplicate the existing terminal
  session and input-arbitration boundary.
