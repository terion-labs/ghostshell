# Replacing SMBLibrary in an Asura Native AOT build

Asura uses the unmodified `SMBLibrary` NuGet package version `1.5.7.1`
under `LGPL-3.0-or-later`. The macOS release compiles that managed library into
the Native AOT executable. It does not ship a separately replaceable
`SMBLibrary.dll`.

This repository contains Asura's corresponding application source and
build scripts. The steps below let a recipient replace SMBLibrary with a
modified build and produce a new Asura executable. This is engineering
evidence for review, not a claim of legal clearance.

## Obtain the linked source

The reviewed upstream source corresponding to package version 1.5.7.1 is
commit `255339717ccc9a278579d563f42939d9f2668506`. That commit sets
`SMBLibrary.csproj` to 1.5.7.1 and predates the matching NuGet publication.
The NuGet metadata does not carry a repository commit, so this mapping remains
an explicit review item rather than a cryptographic claim about the publisher's
build input. Upstream did not create a `v1.5.7.1` tag. Download the
commit-addressed archive recorded in `SMBLIBRARY-SOURCE.json` and verify its SHA-256
before using it:

```sh
curl -fL \
  https://codeload.github.com/TalAloni/SMBLibrary/tar.gz/255339717ccc9a278579d563f42939d9f2668506 \
  -o SMBLibrary-1.5.7.1.tar.gz
shasum -a 256 SMBLibrary-1.5.7.1.tar.gz
```

The expected digest is
`0f4ab1c1f6144eb383c8cd68c9e4eeba3d9bae780e1f0a8fdefcc848223ae64d`.
The archive's `SMBLibrary/SMBLibrary.csproj` declares version `1.5.7.1` and
`LGPL-3.0-or-later`.

## Build a replacement package

Extract the archive, make the desired changes, and keep those changes under the
applicable LGPL terms. Build the package with the pinned Asura SDK:

```sh
./.dotnet/dotnet pack \
  /path/to/SMBLibrary/SMBLibrary/SMBLibrary.csproj \
  --configuration Debug \
  --output /path/to/local-feed
```

Debug configuration avoids upstream's Windows-only release `ILRepack` target.
The resulting package must retain identity `SMBLibrary/1.5.7.1`. If a modified
package uses another version, update `Directory.Packages.props` to that version.

## Point Asura at the replacement

Obtain Asura's application source from
<https://github.com/terion-labs/asura>. For a published build, check out
the `v<version>` tag matching the application's `CFBundleShortVersionString`;
GitHub also attaches that tag's source archive to the release. Work in a copy
of that source tree. Add the local feed to the copy's `NuGet.Config`, map
`SMBLibrary` to it, and leave all other packages mapped to NuGet.org. Remove
the cached `smblibrary/1.5.7.1` directory from the copy's `.nuget/packages`
directory.

Regenerate every affected checked-in lock file with `--force-evaluate`. At a
minimum this includes the lock files under `src/Asura.Files` and
`src/Asura.Desktop`, including
`src/Asura.Desktop/packages.osx-arm64.aot.lock.json`. Review the diff and
confirm that only the intended SMBLibrary version and content hashes changed.

Update the `SMBLibrary/1.5.7.1` entry in
`licenses/managed-components.json` as well. Copy `contentHash` from the restored
package's `.nuget/packages/smblibrary/1.5.7.1/.nupkg.metadata` file and copy
`nupkgSha512` from
`.nuget/packages/smblibrary/1.5.7.1/smblibrary.1.5.7.1.nupkg.sha512`. Record the
modified source archive and its digest in `licenses/SMBLIBRARY-SOURCE.json`, and
set its `distribution.modified` value to `true`.

Finally, update the matching SHA-256 values in
`licenses/macos-release-legal.json` for the changed managed catalog and SMB
source record. Keep `legalClearance` false, retain a release blocker, and keep
the SMB/managed disposition at `pending-project-owner-decision`. Set the review
`basis`, `reviewedBy`, and `reviewedAtUtc` fields to null. This allows a
local relinked package while preventing it from being published as a cleared
release.

Build the native dependencies and package the replacement executable:

```sh
ASURA_SKIP_NATIVE=1 ./scripts/bootstrap.sh
./scripts/build-libghostty-vt.sh --rid osx-arm64
./scripts/build-sql-language-worker.sh --local --rid osx-arm64
./scripts/build-cef-runtime.sh --rid osx-arm64
./scripts/package-macos.sh \
  --version 0.1.0 \
  --build-version 1 \
  --runtime-identifier osx-arm64 \
  --output artifacts/relinked/Asura.app
```

The packaging command performs the Native AOT compile and produces the
replacement `Contents/MacOS/Asura` executable. Developer ID signing and
notarization are not required to exercise or inspect a locally rebuilt copy.

Asura imposes no term that forbids reverse engineering for debugging a
modified SMBLibrary. The Asura source remains under MIT. SMBLibrary and
any modifications to it remain governed by `LGPL-3.0-or-later`.
