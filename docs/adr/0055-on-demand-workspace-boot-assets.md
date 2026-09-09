# ADR 0055: On-demand workspace boot assets

The app bundles the signed host runtime, its required Swift libraries, and a
`boot-assets.json` descriptor. It does not bundle the Linux kernel, guest boot
filesystem, or source-distribution archives.

Release packaging produces `Asura-workspace-boot-arm64.zip` alongside the
app. The signed descriptor pins the archive and each image by SHA-256 and exact
size. On first isolated-workspace preparation, the host downloads that asset
from the matching Asura version's GitHub release. This bootstrap download
precedes the guest and does not carry workspace traffic.

Images are cached in the app data directory at `sdk-workspaces/boot/<archive hash>`.
All workspaces share the cache. A cross-process lock serializes provisioning,
downloads and extraction are bounded, and each file is verified before atomic
publication. Every new VM preparation verifies the cached images. Cache hits
work offline. Cancellation and failures leave no usable partial image, and
retrying preparation retries the download. Progress appears in the existing
workspace-preparation view. The cache is independent of workspace recreation
and app replacement.

Development uses the same verification path with a locally built sidecar,
selected by the development launcher's `ASURA_WORKSPACE_BOOT_ARCHIVE`.
Changing that path cannot bypass the descriptor's hashes. No development
sidecar is copied into the app.

`Asura-networking-sources.zip` is a separate release asset with the kernel
source, configuration, Kata patches/build material, and OpenConnect source and
relinking instructions. License notices and source-location instructions remain
in the app. Packaging rejects any boot image or source tarball inside the app
and verifies that the boot sidecar matches its signed descriptor before emitting
release assets. Both sidecars and their checksum files are uploaded in the same
release creation operation as the app and updater artifacts.
