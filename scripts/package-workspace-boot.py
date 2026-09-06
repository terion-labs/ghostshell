#!/usr/bin/env python3
"""Build the separately downloaded boot payload and its app-pinned descriptor."""
import hashlib
import json
import pathlib
import shutil
import sys
import zipfile


def digest(path):
    checksum = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(block)
    return checksum.hexdigest()


def main():
    runtime = pathlib.Path(sys.argv[1])
    destination = pathlib.Path(sys.argv[2])
    destination.mkdir(parents=True, exist_ok=True)
    archive = destination / "GhostShell-workspace-boot-arm64.zip"
    temporary = archive.with_suffix(".partial")
    files = {}
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED) as bundle:
            for name in ("kernel.bin", "initfs.ext4"):
                path = runtime / name
                files[name] = {"sha256": digest(path), "size": path.stat().st_size}
                # Fixed metadata keeps repeated packaging of the same images identical.
                entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
                entry.compress_type = zipfile.ZIP_DEFLATED
                entry.external_attr = 0o100600 << 16
                with path.open("rb") as source, bundle.open(entry, "w") as target:
                    shutil.copyfileobj(source, target)
        temporary.replace(archive)
    finally:
        temporary.unlink(missing_ok=True)
    descriptor = {"sha256": digest(archive), "size": archive.stat().st_size, "files": files}
    (runtime / "boot-assets.json").write_text(json.dumps(descriptor, indent=2) + "\n")
    archive.with_suffix(".zip.sha256").write_text(f"{descriptor['sha256']}  {archive.name}\n")


if __name__ == "__main__":
    main()
