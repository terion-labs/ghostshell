# Asura libghostty-vt overlay

These patches extend the public `libghostty-vt` C ABI while Asura tracks
Ghostty commit `08f039fbb3dea9c6b1cdb5ff4550666598122346`. They are applied in lexical
order to a disposable upstream checkout by the native build pipeline; the
Ghostty checkout itself is never committed.

`0001-asura-vt-extensions.patch` adds two deliberately narrow APIs and
enables one existing upstream implementation for the library build:

- `GHOSTTY_TERMINAL_OPT_SEMANTIC_PROMPT` (`31`) installs a synchronous
  `GhosttyTerminalSemanticPromptFn`. It is invoked only after Ghostty has
  successfully applied an OSC 133 action. A/N/P normalize to `PROMPT`, B/I to
  `INPUT`, C to `EXECUTED`, and D to `FINISHED`; D also reports its optional
  signed exit status. L remains a state-only fresh-line operation and emits no
  lifecycle event. The event pointer is borrowed for the callback duration and
  the callback must not re-enter `ghostty_terminal_vt_write()`.
- `GhosttyKittyGraphicsVirtualPlacementIterator` enumerates Unicode Kitty
  placeholder instances in the active viewport. Reset takes the renderer's
  cell pixel dimensions, and `next` returns source/destination rectangles,
  offsets, viewport coordinates, image/placement IDs, and z-order. Its
  implementation directly calls Ghostty's own `unicode.placementIterator` and
  `Placement.renderPlacement`, so Asura does not fork the placement
  algorithm. Any terminal mutation invalidates iteration; reset before reading
  again. Normal exhaustion is `GHOSTTY_NO_VALUE`.
- libghostty-vt now includes Ghostty's existing Wuffs module and installs
  `decodePngWuffs` as its default `terminal_sys.decode_png` implementation.
  Kitty PNG bytes are decoded directly into storage using Ghostty's allocator;
  no managed callback or allocator crossing is required. The existing
  `GHOSTTY_SYS_OPT_DECODE_PNG` remains available to replace the default, while
  explicitly setting it to NULL disables PNG decoding.

`0002-asura-search-and-abi.patch` makes the managed/native contract
fail closed and delegates search semantics to Ghostty itself:

- `ghostty_asura_extension_abi()` returns the exact Asura extension
  ABI (`1`). The managed runtime probe requires that exact value in addition
  to every C entry point imported by the binding, so an upstream or stale
  library cannot pass discovery and fail later at first use.
- `ghostty_terminal_search()` is a synchronous, bounded wrapper over Ghostty's
  canonical `ScreenSearch`. It searches active content and scrollback,
  preserves wrapped-row and cross-page matching, orders results newest first,
  wraps navigation indices, installs the selected match, and scrolls only when
  the selection is outside the viewport. Calls retain at most 4096 reported
  matches and set `scan_truncated` when additional history was not scanned.
- Compile-time Zig assertions lock the 64-bit layouts of both search structs,
  the semantic-prompt event, and virtual Kitty placement geometry to the
  layouts consumed by the managed binding.

The patch carries upstream Zig unit tests for the complete normalized OSC 133
lifecycle, callback clearing and userdata, virtual-placeholder geometry,
iterator exhaustion, invalid dimensions/lifecycle, and the bundled Wuffs PNG
decoder. It was validated from a clean checkout with:

```sh
zig build test-lib-vt -Demit-lib-vt=true -Demit-xcframework=false
zig build -Demit-lib-vt=true -Demit-xcframework=false -Doptimize=ReleaseFast
```

The installed C headers also compile cleanly as C11 with `-Wall -Wextra
-Werror`. The produced dynamic library is accepted only after the complete
managed import manifest, including the extension-ABI marker and terminal
search entry point, has been verified.

When updating the pinned Ghostty commit, reapply the patch to a clean checkout,
resolve against upstream behavior (never copy the renderer math into
Asura), rerun the upstream tests, and regenerate the patch with
`git format-patch`.
