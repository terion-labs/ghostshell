#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
package_dir="${repository_dir}/native/workspace-runtime"
artifact_dir="${repository_dir}/native/artifacts/osx-arm64/workspace-runtime"
build_dir="${repository_dir}/native/artifacts/workspace-runtime-build"
# Release source exports are sealed read-only. Keep SwiftPM products and locked
# dependency checkouts with the other writable native build artifacts.
swift_scratch_dir="${build_dir}/swift"
entitlements="${repository_dir}/tools/Asura.Packaging/MacOS/WorkspaceRuntime.entitlements"
# Swift Testing is supplied by full Xcode, not every Command Line Tools SDK.
if [[ -z "${DEVELOPER_DIR:-}" && -d /Applications/Xcode.app/Contents/Developer ]]; then
    export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
fi
sdk_version="0.42.0"
sdk_revision="c0185aea5c04fcd4d1cfe9359e0066a380835403"
vminit_image="ghcr.io/apple/containerization/vminit@sha256:cde8a93f9861c664bf2b74b4e2893cf877680806f12e7e989eb9c51b2f2e93bf"
kernel_archive="kata-static-3.32.0-arm64.tar.zst"
kernel_archive_sha256="8736c054d9223974735394f822000823baef509e1c33405ec798240fa9b6e4b5"
kernel_member="opt/kata/share/kata-containers/vmlinux-6.18.35-197-debug"
linux_archive="linux-6.18.35.tar.xz"
linux_sha256="f78602932219125e211c5f5bfd84edcfd4ec5ce88fc944f8248413f665bef236"
kata_source_archive="kata-containers-3.32.0.tar.gz"
kata_source_sha256="722a022d873e6742788eca9f6ed74e8b7c4f70ffe4713144bd2e91fc3db620d0"

source_manifest() {
    (
        cd "${repository_dir}"
        shasum -a 256 native/workspace-runtime/Package.swift native/workspace-runtime/Package.resolved \
            scripts/build-workspace-runtime.sh scripts/package-workspace-boot.py tools/Asura.Packaging/MacOS/WorkspaceRuntime.entitlements
        find native/workspace-runtime/Sources -type f -print | LC_ALL=C sort | while IFS= read -r path; do
            shasum -a 256 "${path}"
        done
    )
}

# Swift's precompiled modules also embed absolute checkout paths. Clean only
# when the checkout moves (or when adopting a cache without a location receipt),
# retaining the locked dependency checkouts and ordinary incremental builds.
if [[ $# -eq 0 || "${1:-}" == --test ]]; then
    location_receipt="${swift_scratch_dir}/asura-build-location"
    if [[ ! -f "${location_receipt}" || "$(cat "${location_receipt}")" != "${package_dir}" ]]; then
        xcrun swift package --package-path "${package_dir}" --scratch-path "${swift_scratch_dir}" clean
        mkdir -p "${swift_scratch_dir}"
        printf '%s\n' "${package_dir}" > "${location_receipt}"
    fi
fi

case "${1:-}" in
    --help|-h)
        echo "Usage: ./scripts/build-workspace-runtime.sh [--test|--verify]"
        exit 0 ;;
    --verify)
        cd "${artifact_dir}"
        shasum -a 256 -c legal/MANIFEST.sha256
        codesign --verify --strict workspace-runtime
        codesign --display --entitlements :- workspace-runtime 2>/dev/null \
            | python3 -c 'import plistlib,sys; assert plistlib.loads(sys.stdin.buffer.read())=={"com.apple.security.virtualization":True}, "Unexpected workspace runtime entitlements"'
        diff -u "${artifact_dir}/legal/SOURCE-MANIFEST.sha256" <(source_manifest)
        cd "${build_dir}/distribution"
        shasum -a 256 -c Asura-workspace-boot-arm64.zip.sha256
        expected_boot_sha="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sha256"])' "${artifact_dir}/boot-assets.json")"
        [[ "$(shasum -a 256 Asura-workspace-boot-arm64.zip | awk '{print $1}')" == "${expected_boot_sha}" ]]
        exit 0 ;;
    --test)
        xcrun swift test --package-path "${package_dir}" --scratch-path "${swift_scratch_dir}" --disable-automatic-resolution
        exit 0 ;;
    "") ;;
    *) echo "Unknown workspace runtime build option." >&2; exit 64 ;;
esac
if [[ $# -ne 0 || "$(uname -s):$(uname -m)" != Darwin:arm64 ]]; then
    echo "The workspace runtime payload requires an Apple Silicon macOS build host." >&2
    exit 64
fi
for command in curl shasum tar codesign otool install_name_tool xcrun python3; do
    command -v "${command}" >/dev/null || { echo "Missing build tool: ${command}" >&2; exit 1; }
done
[[ -f "${package_dir}/Package.resolved" ]] || { echo "The reviewed Swift dependency lock is missing." >&2; exit 1; }
python3 -c 'import json,sys; p=next(p for p in json.load(open(sys.argv[1]))["pins"] if p["identity"]=="containerization"); assert p["state"]=={"revision":sys.argv[2],"version":sys.argv[3]}, "Unreviewed Containerization dependency"' \
    "${package_dir}/Package.resolved" "${sdk_revision}" "${sdk_version}"

mkdir -p "${build_dir}/downloads" "$(dirname "${artifact_dir}")"
staging_dir="$(mktemp -d "${build_dir}/payload.XXXXXX")"
trap 'rm -rf -- "${staging_dir}"' EXIT
mkdir -p "${staging_dir}/legal/sources"
source_manifest > "${staging_dir}/legal/SOURCE-MANIFEST.sha256"

download() {
    local url="$1" name="$2" expected="$3"
    local destination="${build_dir}/downloads/${name}"
    if [[ ! -f "${destination}" ]]; then
        curl --fail --location --silent --show-error "${url}" --output "${destination}.partial"
        mv "${destination}.partial" "${destination}"
    fi
    local actual
    actual="$(shasum -a 256 "${destination}" | awk '{print $1}')"
    [[ "${actual}" == "${expected}" ]] || { echo "Unreviewed workspace asset checksum: ${name}" >&2; exit 1; }
}

download "https://github.com/kata-containers/kata-containers/releases/download/3.32.0/${kernel_archive}" \
    "${kernel_archive}" "${kernel_archive_sha256}"
download "https://cdn.kernel.org/pub/linux/kernel/v6.x/${linux_archive}" "${linux_archive}" "${linux_sha256}"
download "https://codeload.github.com/kata-containers/kata-containers/tar.gz/refs/tags/3.32.0" \
    "${kata_source_archive}" "${kata_source_sha256}"
download "https://raw.githubusercontent.com/swiftlang/swift/swift-6.3-RELEASE/LICENSE.txt" \
    SWIFT-LICENSE.txt 770af8291f708538d8ff885a0bbc4e045cd700531741c4f99528d435c14d7f55
download "https://git.musl-libc.org/cgit/musl/plain/COPYRIGHT?h=v1.2.5" \
    MUSL-COPYRIGHT.txt f9bc4423732350eb0b3f7ed7e91d530298476f8fec0c6c427a1c04ade22655af

xcrun swift build --package-path "${package_dir}" --scratch-path "${swift_scratch_dir}" -c release --disable-automatic-resolution
"${script_dir}/build-workspace-runtime.sh" --test
binary_dir="$(xcrun swift build --package-path "${package_dir}" --scratch-path "${swift_scratch_dir}" -c release --show-bin-path)"
diff -u "${staging_dir}/legal/SOURCE-MANIFEST.sha256" <(source_manifest)
install -m 755 "${binary_dir}/workspace-runtime" "${staging_dir}/workspace-runtime"
# Swift compatibility libraries are not necessarily present on the deployment OS.
# The selected toolchain knows its versioned compatibility-library directories,
# even when the linker did not emit an absolute toolchain search path.
xcrun swift-stdlib-tool --copy --platform macosx --scan-executable "${staging_dir}/workspace-runtime" \
    --destination "${staging_dir}" --sign -
# Remove host-only search paths after copying the required library closure.
while IFS= read -r runtime_path; do
    [[ "${runtime_path}" == /* && "${runtime_path}" != /usr/lib/* ]] || continue
    install_name_tool -delete_rpath "${runtime_path}" "${staging_dir}/workspace-runtime"
done < <(otool -l "${staging_dir}/workspace-runtime" | awk '/cmd LC_RPATH/{getline; getline; print $2}')
# The Swift signing tool leaves an unsigned backup beside each copied library.
for backup in "${staging_dir}/"*.dylib.original; do
    [[ -f "${backup}" ]] || continue
    rm -- "${backup}"
done
for resource_bundle in "${binary_dir}/"*.bundle; do
    [[ -d "${resource_bundle}" ]] || continue
    cp -R "${resource_bundle}" "${staging_dir}/"
done
codesign --force --sign - --entitlements "${entitlements}" "${staging_dir}/workspace-runtime"
codesign --verify --strict "${staging_dir}/workspace-runtime"
for executable in "${staging_dir}/workspace-runtime" "${staging_dir}/"*.dylib; do
    [[ -f "${executable}" ]] || continue
    while IFS= read -r dependency; do
        case "${dependency}" in
            /usr/lib/*|/System/Library/*) ;;
            @rpath/libswift*.dylib)
                [[ -f "${staging_dir}/${dependency#@rpath/}" ]] || { echo "Missing bundled Swift library: ${dependency}" >&2; exit 1; } ;;
            *) echo "Workspace runtime contains an unbundled non-system library: ${dependency}" >&2; exit 1 ;;
        esac
    done < <(otool -L "${executable}" | tail -n +2 | awk '{print $1}')
done

tar -xOf "${build_dir}/downloads/${kernel_archive}" "./${kernel_member}" > "${staging_dir}/kernel.bin"
tar -xOf "${build_dir}/downloads/${kernel_archive}" \
    ./opt/kata/share/kata-containers/config-6.18.35-197-debug > "${staging_dir}/legal/kernel.config"
"${staging_dir}/workspace-runtime" prepare-initfs \
    --image "${vminit_image}" \
    --output "${staging_dir}/initfs.ext4" \
    --state-directory "${build_dir}/image-cache"
cp "${build_dir}/downloads/${linux_archive}" "${staging_dir}/legal/sources/"
cp "${build_dir}/downloads/${kata_source_archive}" "${staging_dir}/legal/sources/"
tar -xOf "${build_dir}/downloads/${linux_archive}" linux-6.18.35/LICENSES/preferred/GPL-2.0 \
    > "${staging_dir}/legal/LINUX-GPL-2.0.txt"
cp "${package_dir}/Package.resolved" "${staging_dir}/legal/Package.resolved"
cp "${entitlements}" "${staging_dir}/legal/WorkspaceRuntime.entitlements"
cp "${build_dir}/downloads/SWIFT-LICENSE.txt" "${staging_dir}/legal/"
cp "${build_dir}/downloads/MUSL-COPYRIGHT.txt" "${staging_dir}/legal/"

# Capture licenses from every locked checkout, not just the top-level SDK.
notices="${staging_dir}/legal/THIRD-PARTY-NOTICES.md"
{
    printf '# Workspace runtime dependency notices\n\n'
    printf 'Containerization %s, revision %s. Exact dependency versions are in Package.resolved.\n\n' "${sdk_version}" "${sdk_revision}"
    printf 'The vminit image is %s. Kernel source and build material are in sources/ and kernel.config.\n' "${vminit_image}"
    for checkout in "${swift_scratch_dir}/checkouts/"*; do
        [[ -d "${checkout}" ]] || continue
        licenses="$(find "${checkout}" -type f \( -iname 'LICENSE*' -o -iname 'COPYING*' -o -iname 'NOTICE*' \) ! -path '*/.git/*' -print | LC_ALL=C sort)"
        [[ -n "${licenses}" ]] || { echo "Missing license for $(basename "${checkout}")." >&2; exit 1; }
        while IFS= read -r license; do
            printf '\n## %s / %s\n\n```text\n' "$(basename "${checkout}")" "${license#${checkout}/}"
            sed 's/```/` ` `/g' "${license}"
            printf '\n```\n'
        done <<< "${licenses}"
    done
} > "${notices}"
cat > "${staging_dir}/legal/SOURCE-AND-BUILD.md" <<EOF
# Workspace runtime source and build

The host helper is built from native/workspace-runtime using the locked Swift
dependencies in Package.resolved. It uses Apple's Virtualization framework and
requires only com.apple.security.virtualization; it does not use vmnet or NAT.
vminit is the unmodified Containerization ${sdk_version} image, pinned by OCI digest:
${vminit_image}
Its source is https://github.com/apple/containerization/tree/${sdk_revision}/vminitd.
The upstream vminit build uses the Swift 6.3 Static Linux SDK; its Swift runtime
license and exception are in SWIFT-LICENSE.txt, and libc notices in MUSL-COPYRIGHT.txt.

The Linux kernel is Kata Containers 3.32.0's debug kernel 6.18.35-197.
Its exact build configuration is kernel.config. Complete corresponding Linux
source is sources/${linux_archive}; Kata's kernel patches and build scripts are
in sources/${kata_source_archive}, tools/packaging/kernel and tools/packaging/static-build.
Follow the Kata kernel build README with the bundled configuration and debug
variant. Upstream binary archive SHA-256: ${kernel_archive_sha256}.
Linux source SHA-256: ${linux_sha256}.
Kata source SHA-256: ${kata_source_sha256}.
The checked-in scripts/build-workspace-runtime.sh reproduces the payload assembly.
Boot images and sources are not part of the app bundle. Download the matching
Asura-workspace-boot-arm64.zip and Asura-networking-sources.zip assets
from the same Asura release. The source paths above refer to that source
package's workspace-runtime directory. MANIFEST.sha256 describes the complete
build payload, including those separately distributed files.
EOF
python3 "${script_dir}/package-workspace-boot.py" "${staging_dir}" "${build_dir}/distribution"
(
    cd "${staging_dir}"
    find . -type f ! -name MANIFEST.sha256 -print | LC_ALL=C sort | while IFS= read -r path; do
        shasum -a 256 "${path#./}"
    done > legal/MANIFEST.sha256
    shasum -a 256 -c legal/MANIFEST.sha256
)
diff -u "${staging_dir}/legal/SOURCE-MANIFEST.sha256" <(source_manifest)
rm -rf -- "${artifact_dir}"
mv "${staging_dir}" "${artifact_dir}"
trap - EXIT
echo "Built bundled workspace runtime in ${artifact_dir}."
