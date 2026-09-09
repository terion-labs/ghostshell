#!/usr/bin/env python3
"""Pin a UI-free Linux backend as a deterministic on-demand archive, never app code."""
import gzip
import hashlib
import json
import os
import pathlib
import stat
import sys
import tarfile

ARCHITECTURE = os.environ.get("GHOSTSHELL_BACKEND_ARCH", "arm64")
if ARCHITECTURE not in ("arm64", "x64"):
    raise ValueError("Unsupported backend architecture")
ARCHIVE = f"GhostShell-workspace-backend-{ARCHITECTURE}.tar.gz"
EXECUTABLE = "GhostShell.Backend"
RUNTIME = "10.0.11"
FORBIDDEN = ("avalonia", "exclr8", "libcef", "chromium", "ghostshell.app.", "ghostshell.browser.", "ghostshell.desktop.")


def digest(path):
    with path.open("rb") as stream:
        checksum = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(block)
        return checksum.hexdigest()


def source_digest(repository):
    # Bind all source inputs conservatively, including new files not committed
    # yet. Never mistake a previously built sidecar for the current source.
    # Also works in the sealed release source tree, which has no Git metadata.
    inputs = list(repository.glob("Directory.*"))
    inputs.extend(repository / name for name in (
        "global.json", "NuGet.Config", "LICENSE", "scripts/build-workspace-backend.sh",
        "scripts/package-workspace-backend.py", "licenses/SMBLIBRARY-LGPL-3.0.txt",
        "licenses/GPL-3.0.txt", "licenses/SMBLIBRARY-SOURCE.json", "licenses/SQLCLIENT-MIT.txt",
        "licenses/SMBLIBRARY-SOURCE-AND-RELINKING.md", "licenses/THIRD-PARTY-NOTICES.md",
        "licenses/workspace-backend-managed-components.json", "licenses/workspace-backend-x64-managed-components.json"))
    excluded = {"bin", "obj", ".git", ".build", "node_modules", "artifacts", "__pycache__"}
    inputs.append(repository / "scripts/build-workspace-network-gateway.sh")
    for source in (repository / "src", repository / "vendor", repository / "tools" / "GhostShell.Packaging", repository / "native/workspace-network-gateway"):
        for directory, children, filenames in os.walk(source):
            children[:] = sorted(child for child in children if child not in excluded)
            if any((pathlib.Path(directory) / child).is_symlink() for child in children):
                raise ValueError(f"Linked backend source directory: {directory}")
            inputs.extend(pathlib.Path(directory) / name for name in filenames)
    checksum = hashlib.sha256()
    for path in sorted(set(inputs)):
        name = path.relative_to(repository).as_posix()
        if not path.exists():
            continue
        if path.is_symlink() or not path.is_file():
            raise ValueError(f"Linked or special backend build input: {name}")
        checksum.update(name.encode() + b"\0" + digest(path).encode() + b"\n")
    return checksum.hexdigest()


def validate_name(name):
    path = pathlib.PurePosixPath(name)
    if (not name or path.is_absolute() or ".." in path.parts or "\\" in name
            or str(path) != name or any(ord(character) < 32 for character in name)):
        raise ValueError(f"Unsafe backend archive path: {name!r}")
    if any(value in name.lower() for value in FORBIDDEN):
        raise ValueError(f"UI/browser dependency in headless backend: {name}")
    if name.lower().endswith((".dylib", ".exe")):
        raise ValueError(f"Non-Linux executable in backend payload: {name}")


def validate_elf(header, name):
    machine = b"\xb7\x00" if ARCHITECTURE == "arm64" else b"\x3e\x00"
    if header[:4] != b"\x7fELF" or header[4:6] != b"\x02\x01" or header[18:20] != machine:
        raise ValueError(f"Backend executable must be a Linux {ARCHITECTURE} ELF: {name}")


def build(repository, destination, payload, expected_source, sdk):
    if source_digest(repository) != expected_source:
        raise ValueError("Backend source changed during publish; rebuild the sidecar")
    files = []
    for path in sorted(payload.rglob("*")):
        if path.is_symlink():
            raise ValueError(f"Linked backend payload: {path}")
        if path.is_dir():
            continue
        if not path.is_file():
            raise ValueError(f"Special backend payload: {path}")
        name = path.relative_to(payload).as_posix()
        validate_name(name)
        files.append((name, path))
    if not any(name == EXECUTABLE for name, _ in files):
        raise ValueError("The backend executable is missing")
    for name, path in files:
        with path.open("rb") as stream:
            header = stream.read(20)
        if name == EXECUTABLE or name.endswith(".so") or header[:4] == b"\x7fELF":
            validate_elf(header, name)
    config = json.loads((payload / f"{EXECUTABLE}.runtimeconfig.json").read_text())
    frameworks = config["runtimeOptions"].get("includedFrameworks", [])
    if frameworks != [{"name": "Microsoft.NETCore.App", "version": RUNTIME}]:
        raise ValueError("Backend must include the pinned self-contained .NET runtime")
    manifest = payload / "MANIFEST.sha256"
    if manifest.exists():
        raise ValueError("Published backend unexpectedly supplied MANIFEST.sha256")
    manifest.write_text("".join(f"{digest(path)}  {name}\n" for name, path in files))
    files.append((manifest.name, manifest))
    files.sort()
    destination.mkdir(parents=True, exist_ok=True)
    archive = destination / ARCHIVE
    temporary = archive.with_suffix(".partial")
    try:
        with temporary.open("wb") as output, gzip.GzipFile(fileobj=output, mode="wb", filename="", mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w|", format=tarfile.PAX_FORMAT) as bundle:
                for name, path in files:
                    entry = tarfile.TarInfo(name)
                    entry.size = path.stat().st_size
                    entry.mode = 0o700 if name == EXECUTABLE else 0o600
                    entry.mtime = entry.uid = entry.gid = 0
                    with path.open("rb") as stream:
                        bundle.addfile(entry, stream)
        temporary.replace(archive)
    finally:
        temporary.unlink(missing_ok=True)
    descriptor = {"sha256": digest(archive), "size": archive.stat().st_size, "executable": EXECUTABLE}
    (destination / "backend-assets.json").write_text(json.dumps(descriptor, indent=2) + "\n")
    (destination / (ARCHIVE + ".sha256")).write_text(f"{descriptor['sha256']}  {ARCHIVE}\n")
    receipt = {"sourceSha256": expected_source, "sdk": sdk, "runtime": RUNTIME, "rid": f"linux-{ARCHITECTURE}", "files": len(files)}
    (destination / "build-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")


def verify(repository, destination):
    descriptor = json.loads((destination / "backend-assets.json").read_text())
    archive = destination / ARCHIVE
    if descriptor != {"sha256": digest(archive), "size": archive.stat().st_size, "executable": EXECUTABLE}:
        raise ValueError("Backend archive differs from its descriptor")
    receipt = json.loads((destination / "build-receipt.json").read_text())
    if receipt["sourceSha256"] != source_digest(repository) or receipt["runtime"] != RUNTIME or receipt["rid"] != f"linux-{ARCHITECTURE}":
        raise ValueError("Backend payload is stale or has a different runtime")
    names = set()
    checksums = {}
    manifest = None
    with tarfile.open(archive, "r:gz") as bundle:
        for entry in bundle:
            validate_name(entry.name)
            if not entry.isfile() or entry.name in names:
                raise ValueError("Backend archive contains links, special entries, or duplicate paths")
            names.add(entry.name)
            expected_mode = 0o700 if entry.name == EXECUTABLE else 0o600
            if stat.S_IMODE(entry.mode) != expected_mode or entry.uid != 0 or entry.gid != 0 or entry.mtime != 0:
                raise ValueError("Backend archive metadata is not deterministic/private")
            with bundle.extractfile(entry) as stream:
                if entry.name == "MANIFEST.sha256":
                    if entry.size > 1024 * 1024:
                        raise ValueError("Backend integrity manifest is oversized")
                    manifest = stream.read().decode("utf-8")
                    continue
                header = stream.read(20)
                if entry.name == EXECUTABLE or entry.name.endswith(".so") or header[:4] == b"\x7fELF":
                    validate_elf(header, entry.name)
                checksum = hashlib.sha256(header)
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    checksum.update(block)
                checksums[entry.name] = checksum.hexdigest()
    if EXECUTABLE not in names or len(names) != receipt["files"]:
        raise ValueError("Backend archive closure is incomplete")
    expected_manifest = "".join(f"{checksum}  {name}\n" for name, checksum in sorted(checksums.items()))
    if manifest != expected_manifest:
        raise ValueError("Backend integrity manifest differs from the archive files")


def verify_release_clearance(repository):
    prefix = "workspace-backend" if ARCHITECTURE == "arm64" else "workspace-backend-x64"
    record = json.loads((repository / f"licenses/{prefix}-release-legal.json").read_text())
    if (record.get("schemaVersion") != 1
            or record.get("format") != "ghostshell-workspace-backend-release-legal-v1"
            or record.get("platform") != f"linux-{ARCHITECTURE}" or record.get("runtime") != RUNTIME):
        raise ValueError("Invalid Linux workspace backend legal record")
    review = record.get("review", {})
    if (record.get("legalClearance") is not True or record.get("releaseBlockers") != []
            or review.get("status") != "accepted-by-project-owner"
            or any(not isinstance(review.get(key), str) or not review[key].strip()
                   for key in ("basis", "reviewedBy", "reviewedAtUtc"))):
        raise ValueError("Linux workspace backend publication is blocked pending its recorded project-owner review (ghostshell-90w9)")
    required = {f"src/GhostShell.Backend/packages.linux-{ARCHITECTURE}.lock.json",
                f"licenses/{prefix}-managed-components.json", "licenses/SMBLIBRARY-SOURCE.json"}
    inputs = record.get("reviewedInputs", {})
    if set(inputs) != required or any(digest(repository / name) != inputs[name] for name in required):
        raise ValueError("Linux workspace backend legal evidence changed after review")


if __name__ == "__main__":
    command, repository = sys.argv[1], pathlib.Path(sys.argv[2])
    if command == "source-digest":
        print(source_digest(repository))
    elif command == "build":
        build(repository, pathlib.Path(sys.argv[3]), pathlib.Path(sys.argv[4]), sys.argv[5], sys.argv[6])
    elif command == "verify":
        verify(repository, pathlib.Path(sys.argv[3]))
    elif command == "verify-release-clearance":
        verify_release_clearance(repository)
    else:
        raise ValueError("Unknown backend packaging operation")
