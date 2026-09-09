#!/usr/bin/env python3
"""Reject retired product names in tracked source, paths, and site copy."""

from pathlib import Path
import re
import subprocess
import sys


repository = Path(__file__).resolve().parent.parent
# Split the retired spelling so this check does not match its own source.
retired_name = re.compile(r"ghost" + r"(?:[ _-]?shell|shel\b|<b>shell|logo)", re.IGNORECASE)
tracked_paths = subprocess.check_output(
    ["git", "ls-files", "-z"], cwd=repository
).decode().split("\0")
failures = []
for relative_path in tracked_paths:
    if not relative_path:
        continue
    # These are immutable observations of older builds, with recorded hashes.
    # Rewriting them would misrepresent what was actually tested.
    if relative_path.startswith((
        "docs/acceptance/linux-arm64-xvfb/",
        "docs/acceptance/platform-vault/",
    )):
        continue
    if retired_name.search(relative_path):
        failures.append(f"Retired product name in path: {relative_path}")
    path = repository / relative_path
    if not path.is_file():
        failures.append(f"Tracked path is missing: {relative_path}")
        continue
    try:
        content = path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        continue
    for number, line in enumerate(content.splitlines(), 1):
        if retired_name.search(line):
            failures.append(f"Retired product name: {relative_path}:{number}")

if failures:
    print("\n".join(failures), file=sys.stderr)
    sys.exit(1)
print("Product naming passed: tracked source, paths, and site copy use Asura.")
