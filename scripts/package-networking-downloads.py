#!/usr/bin/env python3
"""Stage release sidecars, verify their pins, and reject boot/source app bloat."""
import hashlib
import json
import pathlib
import shutil
import sys
import zipfile


def digest(path):
    with path.open("rb") as stream:
        return stream_digest(stream)


def stream_digest(stream):
    checksum = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        checksum.update(block)
    return checksum.hexdigest()


def main():
    repository, app, output = map(pathlib.Path, sys.argv[1:])
    runtime = repository / "native/artifacts/osx-arm64/workspace-runtime"
    engines = repository / "native/artifacts/osx-arm64/connection-engines"
    descriptor = app / "Contents/Resources/runtimes/osx-arm64/workspace-runtime/boot-assets.json"
    pin = json.loads(descriptor.read_text())
    for path in app.rglob("*"):
        if path.name in ("kernel.bin", "initfs.ext4") or path.name.endswith((".tar.xz", ".tar.gz")):
            raise ValueError(f"On-demand or source payload leaked into app: {path}")
    boot = repository / "native/artifacts/workspace-runtime-build/distribution/GhostShell-workspace-boot-arm64.zip"
    assert digest(boot) == pin["sha256"] and boot.stat().st_size == pin["size"], "Boot sidecar differs from signed app pin"
    with zipfile.ZipFile(boot) as archive:
        assert sorted(archive.namelist()) == sorted(pin["files"])
        for name, expected in pin["files"].items():
            with archive.open(name) as stream:
                assert stream_digest(stream) == expected["sha256"]
            assert archive.getinfo(name).file_size == expected["size"]
    output.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(boot, output / boot.name)
    sources = output / "GhostShell-networking-sources.zip"
    with zipfile.ZipFile(sources, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for root, prefix in ((runtime / "legal", "workspace-runtime"), (engines, "connection-engines")):
            for path in sorted(root.rglob("*")):
                if path.is_file() and (root != engines or path.suffix in (".txt", ".md", ".sha256", ".gz")):
                    archive.write(path, f"{prefix}/{path.relative_to(root)}")
    for path in (output / boot.name, sources):
        path.with_suffix(".zip.sha256").write_text(f"{digest(path)}  {path.name}\n")


if __name__ == "__main__":
    main()
