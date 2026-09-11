#!/usr/bin/env python3
"""Scope the pinned CEF framework's Keychain service before code signing.

CEF 150 has no API for this name. Replace its one complete service literal,
without changing Mach-O offsets or OSCrypt's random key/encryption behavior.
Remove this build-time compatibility patch when the pinned distribution exposes
CEF's keychain_service_name setting (upstream cef#4247).
"""

import argparse
import os
from pathlib import Path
import stat
import tempfile


SERVICES = {
    "upstream": b"Chromium Safe Storage\0",
    "release": b"Asura Browser Storage\0",
    "development": b"Asura Dev CEF Storage\0",
}


def scope_framework(path: Path, source: str, target: str) -> bool:
    if path.is_symlink() or not path.is_file():
        raise ValueError("CEF framework must be a regular, unlinked file.")
    before, after = SERVICES[source], SERVICES[target]
    data = path.read_bytes()
    if data[:4] != b"\xcf\xfa\xed\xfe":
        raise ValueError("Expected a thin little-endian 64-bit Mach-O framework.")
    if len(before) != len(after):
        raise ValueError("Keychain service names must have identical byte lengths.")
    counts = {name: data.count(value) for name, value in SERVICES.items()}
    if counts[target] == 1 and sum(counts.values()) == 1:
        return False
    if counts[source] != 1 or sum(counts.values()) != 1:
        raise ValueError("CEF Keychain service differs from the pinned framework.")
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as output:
            temporary = Path(output.name)
            output.write(data.replace(before, after))
            output.flush()
            os.fsync(output.fileno())
        temporary.chmod(stat.S_IMODE(path.stat().st_mode))
        temporary.replace(path)
        temporary = None
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
    return True


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("framework", type=Path)
    parser.add_argument("--source", choices=SERVICES, default="upstream")
    parser.add_argument("--target", choices=("release", "development"), default="release")
    arguments = parser.parse_args()
    scope_framework(arguments.framework, arguments.source, arguments.target)
