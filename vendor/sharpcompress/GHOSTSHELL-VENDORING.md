# SharpCompress central-directory enumeration

Upstream: https://github.com/adamhathcock/sharpcompress
Version: 0.50.3, commit `67bd9289f99dc77e1b65730a08e8213405921488`.
Source archive: https://codeload.github.com/adamhathcock/sharpcompress/tar.gz/67bd9289f99dc77e1b65730a08e8213405921488
SHA-256: `9a3a4d57b279243ce24332fbb342c152b5c286172fa57881d9536e83c777afeb`.
License: MIT, preserved in `upstream/LICENSE.txt`.

Only upstream library sources, editor configuration, license, and public signing key are included;
upstream test fixtures and assets are not build or release inputs. The net10
wrapper uses upstream's AOT-compatible target and has no package dependencies.

`uncached-zip-enumeration.patch` adds one public API wrapping the existing
protected `LoadEntries(Volumes)` iterator. No ZIP parsing logic is changed.
The stock Entries collection retains every traversed header, including entries
skipped for a later page. This iterator lets preview paging retain only its page.
It is single-pass, must remain inside the archive lifetime, and must not run
concurrently with another iterator or archive mutation.

Re-vendoring: extract the three recorded paths from the pinned archive and apply
the separate patch. Do not format upstream source. First-party archive tests
exercise the API, while this third-party tree retains its analyzer boundary.

`UPSTREAM-SOURCE.sha256` records pristine selected source hashes;
`SOURCE-SNAPSHOT.sha256` records patched source plus integration files, excluding
only root `bin`/`obj` output directories, root default and supported per-RID locks, and the root
snapshot inventory itself. Nested bin/obj directories remain source inputs;
explicit Compile globs can consume their files. Git attributes preserve exact
source/patch bytes including upstream CRLF. Paths are relative to this vendor
directory. Regenerate with `node scripts/update-managed-vendor-inventory.mjs sharpcompress /absolute/path/to/pinned-source.tar.gz`.
The generator verifies both archive checksum and exact patch application before
writing inventories; a source/patch mismatch fails rather than being attested.

The active source project uses `packages.lock.json` for the default graph and
`packages.<RID>.lock.json` for linux-x64, linux-arm64, osx-x64, osx-arm64, and
win-x64. Only these exact root lock paths are excluded from the source inventory;
the repository locked-restore guard rejects missing or redirected runtime locks.
Windows-managed and macOS-AOT properties reuse these locks only while repeated
locked restores prove their dependency graphs unchanged. No analyzer policy is imported.
