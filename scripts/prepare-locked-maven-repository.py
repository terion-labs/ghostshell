#!/usr/bin/env python3
"""Materialize and verify the SQL worker's Maven repository without Maven."""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import re
import ssl
import stat
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request


CENTRAL_REPOSITORY = "https://repo.maven.apache.org/maven2/"
LOCK_FORMAT_VERSION = 1
MAXIMUM_ARTIFACTS = 2_000
MAXIMUM_ARTIFACT_BYTES = 256 * 1024 * 1024
MAXIMUM_TOTAL_BYTES = 1024 * 1024 * 1024
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
READ_ONLY_FILE_MODE = stat.S_IRUSR | stat.S_IRGRP | stat.S_IROTH
READ_ONLY_DIRECTORY_MODE = (
    READ_ONLY_FILE_MODE | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH
)
WRITE_MODE_MASK = stat.S_IWUSR | stat.S_IWGRP | stat.S_IWOTH
WINDOWS_READ_ONLY_PRINCIPAL = "*S-1-1-0"
MAVEN_PATH_PATTERN = re.compile(
    r"^[A-Za-z0-9_.+-]+(?:/[A-Za-z0-9_.+-]+)*\.(?:jar|pom)$"
)


class RejectRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, file_pointer, code, message, headers, new_url):
        raise urllib.error.HTTPError(
            request.full_url,
            code,
            "Maven content redirects are not permitted",
            headers,
            file_pointer,
        )


def read_lock(path: pathlib.Path) -> list[dict[str, object]]:
    document = json.loads(path.read_text(encoding="utf-8"))
    if set(document) != {"formatVersion", "repository", "artifacts"}:
        raise ValueError("Maven content lock has unexpected top-level fields")
    if document["formatVersion"] != LOCK_FORMAT_VERSION:
        raise ValueError("Unsupported Maven content lock format")
    if document["repository"] != CENTRAL_REPOSITORY:
        raise ValueError("Maven content lock must use the reviewed Central endpoint")

    artifacts = document["artifacts"]
    if not isinstance(artifacts, list) or not 0 < len(artifacts) <= MAXIMUM_ARTIFACTS:
        raise ValueError("Maven content lock artifact count is invalid")

    previous = ""
    total_size = 0
    for artifact in artifacts:
        if not isinstance(artifact, dict) or set(artifact) != {"path", "size", "sha256"}:
            raise ValueError("Maven content lock artifact has unexpected fields")
        relative = artifact["path"]
        size = artifact["size"]
        digest = artifact["sha256"]
        if not isinstance(relative, str) or MAVEN_PATH_PATTERN.fullmatch(relative) is None:
            raise ValueError("Maven content lock contains an unsupported path")
        parts = pathlib.PurePosixPath(relative).parts
        if not parts or relative.startswith("/") or any(part in {"", ".", ".."} for part in parts):
            raise ValueError("Maven content lock contains an unsafe path")
        if relative <= previous:
            raise ValueError("Maven content lock paths must be unique and sorted")
        if not isinstance(size, int) or isinstance(size, bool) or not 0 < size <= MAXIMUM_ARTIFACT_BYTES:
            raise ValueError(f"Maven content lock has an invalid size for {relative}")
        if not isinstance(digest, str) or SHA256_PATTERN.fullmatch(digest) is None:
            raise ValueError(f"Maven content lock has an invalid digest for {relative}")
        previous = relative
        total_size += size
    if total_size > MAXIMUM_TOTAL_BYTES:
        raise ValueError("Maven content lock exceeds the total byte limit")
    return artifacts


def download_artifact(
    opener: urllib.request.OpenerDirector,
    artifact: dict[str, object],
    destination: pathlib.Path,
) -> None:
    relative = str(artifact["path"])
    expected_size = int(artifact["size"])
    expected_digest = str(artifact["sha256"])
    target = destination.joinpath(*pathlib.PurePosixPath(relative).parts)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(f".{target.name}.{os.getpid()}.part")
    request = urllib.request.Request(
        CENTRAL_REPOSITORY + urllib.parse.quote(relative, safe="/"),
        headers={"User-Agent": "Asura-Maven-Lock/1"},
    )
    digest = hashlib.sha256()
    written = 0
    try:
        with opener.open(request, timeout=60) as response, temporary.open("xb") as output:
            if response.geturl() != request.full_url:
                raise ValueError(f"Maven content URL changed for {relative}")
            while chunk := response.read(1024 * 1024):
                written += len(chunk)
                if written > expected_size:
                    raise ValueError(f"Maven content exceeds locked size for {relative}")
                digest.update(chunk)
                output.write(chunk)
        if written != expected_size or digest.hexdigest() != expected_digest:
            raise ValueError(f"Maven content does not match lock for {relative}")
        os.replace(temporary, target)
        target.chmod(0o444)
    finally:
        temporary.unlink(missing_ok=True)


def make_read_only(path: pathlib.Path, mode: int) -> None:
    path.chmod(mode)
    if os.name == "nt":
        return
    actual_mode = stat.S_IMODE(path.stat(follow_symlinks=False).st_mode)
    if actual_mode & WRITE_MODE_MASK:
        raise ValueError(f"Could not make locked Maven repository path read-only: {path}")


def run_icacls(*arguments: str) -> None:
    result = subprocess.run(
        ["icacls", *arguments],
        check=False,
        capture_output=True,
        text=True,
        timeout=60,
    )
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip()
        raise ValueError(f"Could not update locked Maven repository ACL: {detail}")


def deny_windows_repository_writes(destination: pathlib.Path) -> None:
    run_icacls(
        str(destination),
        "/deny",
        f"{WINDOWS_READ_ONLY_PRINCIPAL}:(OI)(CI)(W,D,DC)",
        "/T",
    )
    probe = destination / f".asura-write-probe-{os.getpid()}"
    try:
        probe.mkdir()
    except PermissionError:
        return
    run_icacls(str(destination), "/remove:d", WINDOWS_READ_ONLY_PRINCIPAL, "/T")
    probe.rmdir()
    raise ValueError("Windows ACL did not make the locked Maven repository read-only")


def seal_repository(destination: pathlib.Path) -> None:
    """Remove write permission from every verified file and directory."""
    for root, directory_names, file_names in os.walk(
        destination,
        topdown=False,
        followlinks=False,
    ):
        root_path = pathlib.Path(root)
        for name in file_names:
            path = root_path / name
            if path.is_symlink() or not path.is_file():
                raise ValueError(f"Locked Maven repository contains an unsupported entry: {path}")
            make_read_only(path, READ_ONLY_FILE_MODE)
        for name in directory_names:
            path = root_path / name
            if path.is_symlink() or not path.is_dir():
                raise ValueError(f"Locked Maven repository contains an unsupported entry: {path}")
        make_read_only(root_path, READ_ONLY_DIRECTORY_MODE)
    if os.name == "nt":
        deny_windows_repository_writes(destination)


def unseal_repository(destination: pathlib.Path) -> None:
    if os.name == "nt":
        run_icacls(str(destination), "/remove:d", WINDOWS_READ_ONLY_PRINCIPAL, "/T")
    for root, directory_names, file_names in os.walk(destination, topdown=True):
        root_path = pathlib.Path(root)
        root_path.chmod(0o700)
        for name in directory_names:
            path = root_path / name
            if path.is_symlink() or not path.is_dir():
                raise ValueError(f"Locked Maven repository contains an unsupported entry: {path}")
            path.chmod(0o700)
        for name in file_names:
            path = root_path / name
            if path.is_symlink() or not path.is_file():
                raise ValueError(f"Locked Maven repository contains an unsupported entry: {path}")
            path.chmod(0o600)


def main() -> int:
    if len(sys.argv) == 3 and sys.argv[1] == "--unseal":
        destination = pathlib.Path(sys.argv[2])
        if destination.is_symlink():
            raise ValueError("Locked Maven repository destination must not be a symlink")
        unseal_repository(destination.resolve(strict=True))
        return 0
    if len(sys.argv) != 3:
        print(
            f"Usage: {sys.argv[0]} LOCK DESTINATION | --unseal DESTINATION",
            file=sys.stderr,
        )
        return 64

    lock_path = pathlib.Path(sys.argv[1]).resolve(strict=True)
    destination = pathlib.Path(sys.argv[2])
    artifacts = read_lock(lock_path)
    if destination.is_symlink():
        raise ValueError("Locked Maven repository destination must not be a symlink")
    if destination.exists() and any(destination.iterdir()):
        raise ValueError("Locked Maven repository destination must be empty")
    destination.mkdir(parents=True, exist_ok=True)
    opener = urllib.request.build_opener(
        RejectRedirects(),
        urllib.request.HTTPSHandler(context=ssl.create_default_context()),
    )
    for index, artifact in enumerate(artifacts, start=1):
        download_artifact(opener, artifact, destination)
        if index % 50 == 0:
            print(f"Verified {index}/{len(artifacts)} Maven files", file=sys.stderr)
    seal_repository(destination)
    print(f"Verified {len(artifacts)} Maven files in {destination}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
