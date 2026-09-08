#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
dependencies_dir="${repository_dir}/.deps"
source_dir="${dependencies_dir}/ghostty-vt"
patches_dir="${repository_dir}/native/ghostty-vt/patches"
artifact_parent_dir="${repository_dir}/native/artifacts"
component_catalog="${repository_dir}/licenses/native-terminal-components.json"
shell_integration_notice="${repository_dir}/native/ghostty-vt/SHELL-INTEGRATION-NOTICE.md"
required_exports_manifest="${repository_dir}/native/ghostty-vt/required-exports.txt"
extension_abi_probe_source="${repository_dir}/native/ghostty-vt/extension-abi-probe.c"
dotnet="${GHOSTSHELL_DOTNET:-${repository_dir}/.dotnet/dotnet}"
dotnet_artifacts_arguments=()
if [[ -n "${GHOSTSHELL_BUILD_ARTIFACTS_ROOT:-}" ]]; then
    dotnet_artifacts_arguments=(--artifacts-path "${GHOSTSHELL_BUILD_ARTIFACTS_ROOT}")
fi

ghostty_repository="https://github.com/ghostty-org/ghostty.git"
ghostty_commit="08f039fbb3dea9c6b1cdb5ff4550666598122346"
zig_version="0.16.0"
library_version="0.1.0-dev"
ghostshell_extension_abi="1"
ghostshell_extension_export="ghostty_ghostshell_extension_abi"
target_rid=""

usage() {
    cat >&2 <<'EOF'
Usage: ./scripts/build-libghostty-vt.sh [--rid <runtime-identifier>]

Builds the pinned libghostty-vt C ABI and publishes the runtime payload under
native/artifacts/<rid>. Supported targets are osx-arm64, osx-x64, linux-arm64,
linux-x64, and win-x64. The default target is the current host RID. The same
verified build also publishes the pinned cross-platform terminal font assets
under native/artifacts/common.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            target_rid="$2"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage
            exit 64
            ;;
    esac
done

host_os="$(uname -s)"
host_arch="$(uname -m)"
zig_archive_extension="tar.xz"
zig_executable="zig"
case "${host_os}:${host_arch}" in
    Darwin:arm64)
        host_rid="osx-arm64"
        zig_distribution="aarch64-macos"
        zig_archive_sha="b23d70deaa879b5c2d486ed3316f7eaa53e84acf6fc9cc747de152450d401489"
        ;;
    Darwin:x86_64)
        host_rid="osx-x64"
        zig_distribution="x86_64-macos"
        zig_archive_sha="0387557ed1877bc6a2e1802c8391953baddba76081876301c522f52977b52ba7"
        ;;
    Linux:x86_64)
        host_rid="linux-x64"
        zig_distribution="x86_64-linux"
        zig_archive_sha="70e49664a74374b48b51e6f3fdfbf437f6395d42509050588bd49abe52ba3d00"
        ;;
    Linux:aarch64|Linux:arm64)
        host_rid="linux-arm64"
        zig_distribution="aarch64-linux"
        zig_archive_sha="ea4b09bfb22ec6f6c6ceac57ab63efb6b46e17ab08d21f69f3a48b38e1534f17"
        ;;
    MINGW*:*64|MSYS*:*64|CYGWIN*:*64)
        host_rid="win-x64"
        zig_distribution="x86_64-windows"
        zig_archive_sha="68659eb5f1e4eb1437a722f1dd889c5a322c9954607f5edcf337bc3684a75a7e"
        zig_archive_extension="zip"
        zig_executable="zig.exe"
        ;;
    *)
        echo "Unsupported build host ${host_os} ${host_arch}." >&2
        exit 1
        ;;
esac

if [[ -z "${target_rid}" ]]; then
    target_rid="${host_rid}"
fi

case "${target_rid}" in
    osx-arm64)
        zig_target="aarch64-macos.13.0"
        installed_library="lib/libghostty-vt.dylib"
        artifact_library="libghostty-vt.dylib"
        ;;
    osx-x64)
        zig_target="x86_64-macos.13.0"
        installed_library="lib/libghostty-vt.dylib"
        artifact_library="libghostty-vt.dylib"
        ;;
    linux-arm64)
        zig_target="aarch64-linux-gnu"
        installed_library="lib/libghostty-vt.so"
        artifact_library="libghostty-vt.so"
        ;;
    linux-x64)
        zig_target="x86_64-linux-gnu"
        installed_library="lib/libghostty-vt.so"
        artifact_library="libghostty-vt.so"
        ;;
    win-x64)
        zig_target="x86_64-windows-gnu"
        installed_library="bin/ghostty-vt.dll"
        artifact_library="ghostty-vt.dll"
        installed_import_library="lib/libghostty-vt.dll.a"
        ;;
    *)
        echo "Unsupported target RID ${target_rid}." >&2
        exit 64
        ;;
esac

if [[ ! -x "${dotnet}" ]]; then
    echo "Run ./scripts/bootstrap.sh before building the native terminal runtime." >&2
    exit 1
fi
if [[ ! -f "${component_catalog}" ]]; then
    echo "The native terminal component catalog is missing." >&2
    exit 1
fi
if [[ ! -f "${required_exports_manifest}" || -L "${required_exports_manifest}" ]]; then
    echo "The reviewed libghostty-vt export manifest is missing or linked." >&2
    exit 1
fi
if [[ ! -f "${extension_abi_probe_source}" || -L "${extension_abi_probe_source}" ]]; then
    echo "The reviewed libghostty-vt extension ABI probe is missing or linked." >&2
    exit 1
fi
if ! LC_ALL=C sort -c -u "${required_exports_manifest}" 2>/dev/null; then
    echo "The reviewed libghostty-vt export manifest must be sorted and unique." >&2
    exit 1
fi
if grep -Evq '^ghostty_[a-z0-9_]+$' "${required_exports_manifest}"; then
    echo "The reviewed libghostty-vt export manifest contains an invalid symbol." >&2
    exit 1
fi
if [[ -L "${repository_dir}/native" || -L "${artifact_parent_dir}" ]]; then
    echo "The native artifact path must not contain a symbolic-link boundary." >&2
    exit 1
fi
mkdir -p "${dependencies_dir}/toolchains" "${artifact_parent_dir}"

hash_file() {
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        sha256sum "$1" | awk '{print $1}'
    fi
}

file_size() {
    if [[ "${host_os}" == "Darwin" ]]; then
        stat -f '%z' "$1"
    else
        stat -c '%s' "$1"
    fi
}

zig_archive="${dependencies_dir}/toolchains/zig-${zig_distribution}-${zig_version}.${zig_archive_extension}"
zig_dir="${dependencies_dir}/toolchains/zig-${zig_distribution}-${zig_version}"
zig="${zig_dir}/${zig_executable}"
if [[ ! -x "${zig}" ]]; then
    curl -fL \
        "https://ziglang.org/download/${zig_version}/zig-${zig_distribution}-${zig_version}.${zig_archive_extension}" \
        -o "${zig_archive}"
    actual_archive_sha="$(hash_file "${zig_archive}")"
    if [[ "${actual_archive_sha}" != "${zig_archive_sha}" ]]; then
        echo "The downloaded Zig archive failed its pinned SHA-256 check." >&2
        exit 1
    fi
    if [[ "${zig_archive_extension}" == "zip" ]]; then
        tar -xf "${zig_archive}" -C "${dependencies_dir}/toolchains"
    else
        tar -xJf "${zig_archive}" -C "${dependencies_dir}/toolchains"
    fi
fi
if [[ "$("${zig}" version)" != "${zig_version}" ]]; then
    echo "Expected Zig ${zig_version} at ${zig}; found a different version." >&2
    exit 1
fi
if [[ ! -f "${zig_archive}" || "$(hash_file "${zig_archive}")" != "${zig_archive_sha}" ]]; then
    echo "The cached Zig archive does not match the pinned SHA-256." >&2
    exit 1
fi

if [[ ! -d "${source_dir}/.git" ]]; then
    mkdir -p "${source_dir}"
    git -C "${source_dir}" init --quiet
    git -C "${source_dir}" remote add origin "${ghostty_repository}"
    git -C "${source_dir}" fetch --depth 1 origin "${ghostty_commit}"
    git -C "${source_dir}" checkout --detach --quiet FETCH_HEAD
fi
actual_ghostty_commit="$(git -C "${source_dir}" rev-parse HEAD)"
if [[ "${actual_ghostty_commit}" != "${ghostty_commit}" ]]; then
    echo "Expected Ghostty ${ghostty_commit}; found ${actual_ghostty_commit}." >&2
    exit 1
fi
if [[ -n "$(git -C "${source_dir}" status --porcelain --untracked-files=all)" ]]; then
    echo "The pinned libghostty-vt source checkout contains local changes." >&2
    exit 1
fi

build_run_dir="$(mktemp -d "${TMPDIR:-/tmp}/ghostshell-libghostty-vt.XXXXXX")"
artifact_staging_parent="$(mktemp -d "${artifact_parent_dir}/.ghostshell-native-artifacts.XXXXXX")"
build_source_dir="${build_run_dir}/ghostty"
install_dir="${build_run_dir}/install"
local_cache_dir="${build_run_dir}/zig-local-cache"
test_cache_dir="${build_run_dir}/zig-test-cache"
global_cache_dir="${dependencies_dir}/zig-vt-global-cache"
artifact_dir="${artifact_staging_parent}/${target_rid}"
cleanup() {
    rm -rf -- "${build_run_dir}" "${artifact_staging_parent}"
}
trap cleanup EXIT
mkdir -p \
    "${install_dir}" \
    "${local_cache_dir}" \
    "${test_cache_dir}" \
    "${global_cache_dir}" \
    "${artifact_dir}"

# Build from a disposable checkout so the reviewed cache remains immutable and
# tracked GhostSHELL extension patches never accumulate state between builds.
git -c advice.detachedHead=false -c init.defaultBranch=main clone \
    --quiet \
    --shared \
    "${source_dir}" \
    "${build_source_dir}"
git -C "${build_source_dir}" \
    -c advice.detachedHead=false \
    checkout --detach --quiet "${ghostty_commit}"
patch_manifest="${build_run_dir}/patch-manifest.txt"
: > "${patch_manifest}"
patch_count=0
patches=()
if [[ -d "${patches_dir}" ]]; then
    shopt -s nullglob
    patches=("${patches_dir}"/*.patch)
    shopt -u nullglob
    for patch in "${patches[@]}"; do
        git -C "${build_source_dir}" apply --check "${patch}"
        git -C "${build_source_dir}" apply "${patch}"
        printf '%s  %s\n' \
            "$(hash_file "${patch}")" \
            "$(basename "${patch}")" >> "${patch_manifest}"
        patch_count=$((patch_count + 1))
    done
fi
patch_set_sha="$(hash_file "${patch_manifest}")"

# Run Ghostty's unit suite after applying the GhostSHELL-owned patch set. The
# extensions carry their tests in the upstream Zig modules they modify, so a
# successful target build is not enough evidence that their behavior works.
(
    cd -- "${build_source_dir}"
    "${zig}" build test-lib-vt \
        -Demit-lib-vt=true \
        -Demit-xcframework=false \
        --cache-dir "${test_cache_dir}" \
        --global-cache-dir "${global_cache_dir}" \
        --seed 0 \
        -j1
)

build_options=(
    -Demit-lib-vt=true
    -Demit-xcframework=false
    "-Dtarget=${zig_target}"
    -Doptimize=ReleaseFast
    "-Dlib-version-string=${library_version}"
)
(
    cd -- "${build_source_dir}"
    "${zig}" build \
        "${build_options[@]}" \
        --prefix "${install_dir}" \
        --cache-dir "${local_cache_dir}" \
        --global-cache-dir "${global_cache_dir}" \
        --seed 0 \
        -j1
)

library_source="${install_dir}/${installed_library}"
if [[ ! -f "${library_source}" ]]; then
    echo "The libghostty-vt build did not produce ${installed_library}." >&2
    exit 1
fi
cp -L "${library_source}" "${artifact_dir}/${artifact_library}"
cp "${build_source_dir}/LICENSE" "${artifact_dir}/GHOSTTY-LICENSE"
cp "${required_exports_manifest}" \
    "${artifact_dir}/ghostty-vt-required-exports.txt"

exported_symbols="${build_run_dir}/exported-symbols.txt"
if ! nm -g -P "${artifact_dir}/${artifact_library}" 2>/dev/null \
        | awk '{ print $1 }' \
        | sed -E 's/^_(ghostty_)/\1/' \
        | LC_ALL=C sort -u > "${exported_symbols}"; then
    echo "The staged libghostty-vt exports could not be inspected." >&2
    exit 1
fi
while IFS= read -r required_export; do
    if ! grep -Fxq "${required_export}" "${exported_symbols}"; then
        echo "The staged libghostty-vt is missing required export ${required_export}." >&2
        exit 1
    fi
done < "${required_exports_manifest}"

abi_probe="${build_run_dir}/extension-abi-probe"
abi_probe_link_input="${artifact_dir}/${artifact_library}"
abi_probe_link_options=()
if [[ "${target_rid}" == "win-x64" ]]; then
    abi_probe="${abi_probe}.exe"
    abi_probe_link_input="${install_dir}/${installed_import_library}"
elif [[ "${target_rid}" == linux-* ]]; then
    # Zig records the shared library's major-version SONAME even though the
    # release payload deliberately exposes the stable unversioned P/Invoke
    # name. Give the disposable probe a matching runtime alias so its rpath
    # can resolve the exact staged bytes without adding another package file.
    abi_probe_runtime_dir="${build_run_dir}/abi-probe-runtime"
    mkdir -p "${abi_probe_runtime_dir}"
    ln -s \
        "${abi_probe_link_input}" \
        "${abi_probe_runtime_dir}/libghostty-vt.so.${library_version%%.*}"
    abi_probe_link_options+=("-Wl,-rpath,${abi_probe_runtime_dir}")
else
    abi_probe_link_options+=("-Wl,-rpath,${artifact_dir}")
fi
if [[ ! -f "${abi_probe_link_input}" ]]; then
    echo "The staged libghostty-vt ABI link input is unavailable." >&2
    exit 1
fi
"${zig}" cc \
    -std=c11 \
    -Wall \
    -Wextra \
    -Werror \
    -target "${zig_target}" \
    -I "${install_dir}/include" \
    "${extension_abi_probe_source}" \
    "${abi_probe_link_input}" \
    "${abi_probe_link_options[@]}" \
    -o "${abi_probe}"
if [[ "${target_rid}" == "${host_rid}" ]]; then
    if [[ "${target_rid}" == "win-x64" ]]; then
        cp "${artifact_dir}/${artifact_library}" "${build_run_dir}/${artifact_library}"
    fi
    "${abi_probe}"
fi

# Porta.Pty's managed API remains pinned; replace only its Unix native shim.
# This boundary must be receipted with the terminal, never loaded from NuGet.
pty_receipt='null'
if [[ "${target_rid}" != win-* ]]; then
    pty_source="${repository_dir}/native/porta-pty"
    [[ "$(hash_file "${pty_source}/upstream/porta_pty.c")" == 9331e2dff6af7dfd553dba94602321e10e45760ef510b437a27e5afb55c4ec6f ]] || exit 1
    [[ "$(hash_file "${pty_source}/upstream/LICENSE")" == 60957c2a4732512d8f4f86a8511181d435605a4872a1d4ec0456f6fe302d3ed5 ]] || exit 1
    pty_work="${build_run_dir}/pty"
    mkdir "${pty_work}"
    cp "${pty_source}/upstream/porta_pty.c" "${pty_work}/porta_pty.c"
    patch --batch --fuzz=0 -d "${pty_work}" -p1 -i "${pty_source}/descriptor-boundary.patch"
    pty_library=libghostshell_pty.so
    pty_options=(-D_GNU_SOURCE -lutil)
    pty_sdk_options=()
    if [[ "${target_rid}" == osx-* ]]; then
        pty_library=libghostshell_pty.dylib
        pty_sdk="$(xcrun --show-sdk-path)"
        pty_sdk_options=(-isystem "${pty_sdk}/usr/include" "-L${pty_sdk}/usr/lib")
        pty_options=(-D_DARWIN_C_SOURCE -Wl,-install_name,@rpath/libghostshell_pty.dylib)
    fi
    "${zig}" cc -std=c11 -Wall -Wextra -Werror -shared -fPIC -target "${zig_target}" \
        -I "${pty_source}" "${pty_work}/porta_pty.c" "${pty_options[@]}" ${pty_sdk_options[@]+"${pty_sdk_options[@]}"} \
        -o "${artifact_dir}/${pty_library}"
    "${zig}" cc -std=c11 -D_GNU_SOURCE -D_DARWIN_C_SOURCE -Wall -Wextra -Werror -target "${zig_target}" \
        "${pty_source}/descriptor-boundary-test.c" "${artifact_dir}/${pty_library}" ${pty_sdk_options[@]+"${pty_sdk_options[@]}"} \
        "-Wl,-rpath,${artifact_dir}" -o "${pty_work}/descriptor-boundary-test"
    if [[ "${target_rid}" == "${host_rid}" ]]; then
        "${pty_work}/descriptor-boundary-test"
    fi
    cp "${pty_source}/upstream/LICENSE" "${artifact_dir}/PORTA-PTY-LICENSE"
    pty_sha="$(hash_file "${artifact_dir}/${pty_library}")"
    pty_content_sha="${pty_sha}"
    if [[ "${target_rid}" == osx-* ]]; then
        pty_content_sha="$("${dotnet}" run --project "${repository_dir}/tools/GhostShell.Packaging/GhostShell.Packaging.csproj" --configuration Release --verbosity quiet \
            ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
            -- native-signature-removed-sha256 "${artifact_dir}/${pty_library}")"
    fi
    [[ "${pty_content_sha}" =~ ^[a-f0-9]{64}$ ]] || exit 1
    pty_receipt="{\"sourceCommit\":\"54684ba55148ed6bcd0c827ad7e8841a3289a466\",\"sourceSha256\":\"$(hash_file "${pty_source}/upstream/porta_pty.c")\",\"patchSha256\":\"$(hash_file "${pty_source}/descriptor-boundary.patch")\",\"boundarySha256\":\"$(hash_file "${pty_source}/descriptor-boundary.h")\",\"abi\":1,\"artifact\":{\"path\":\"${pty_library}\",\"bytes\":$(file_size "${artifact_dir}/${pty_library}"),\"sha256\":\"${pty_sha}\",\"signatureRemovedSha256\":\"${pty_content_sha}\"},\"license\":{\"path\":\"PORTA-PTY-LICENSE\",\"bytes\":$(file_size "${artifact_dir}/PORTA-PTY-LICENSE"),\"sha256\":\"$(hash_file "${artifact_dir}/PORTA-PTY-LICENSE")\"}}"
fi

# The managed terminal owns rendering but still consumes Ghostty's OSC 133
# shell setup. Stage only the reviewed shells, byte-for-byte from the same
# pinned checkout used for libghostty-vt. The manifest makes that source set
# independently reproducible and package-verifiable.
shell_integration_dir="${artifact_dir}/ghostty/shell-integration"
shell_integration_manifest="${shell_integration_dir}/MANIFEST.sha256"
shell_integration_files=(
    "bash/bash-preexec.sh"
    "bash/ghostty.bash"
    "fish/vendor_conf.d/ghostty-shell-integration.fish"
    "zsh/.zshenv"
    "zsh/ghostty-integration"
)
mkdir -p "${shell_integration_dir}"
: > "${shell_integration_manifest}"
for relative_path in "${shell_integration_files[@]}"; do
    source_path="${build_source_dir}/src/shell-integration/${relative_path}"
    destination_path="${shell_integration_dir}/${relative_path}"
    if [[ ! -f "${source_path}" || -L "${source_path}" ]]; then
        echo "Pinned shell-integration resource is missing or linked: ${relative_path}." >&2
        exit 1
    fi
    mkdir -p "$(dirname "${destination_path}")"
    cp "${source_path}" "${destination_path}"
    printf '%s  %s\n' \
        "$(hash_file "${destination_path}")" \
        "${relative_path}" >> "${shell_integration_manifest}"
done
cp "${shell_integration_notice}" \
    "${shell_integration_dir}/SHELL-INTEGRATION-NOTICE.md"
printf '%s  %s\n' \
    "$(hash_file "${shell_integration_dir}/SHELL-INTEGRATION-NOTICE.md")" \
    "SHELL-INTEGRATION-NOTICE.md" >> "${shell_integration_manifest}"

if [[ "${target_rid}" == osx-* ]]; then
    if ! file "${artifact_dir}/${artifact_library}" \
            | grep -Fq 'Mach-O 64-bit dynamically linked shared library'; then
        echo "The staged libghostty-vt payload is not a macOS dynamic library." >&2
        exit 1
    fi
    if ! otool -D "${artifact_dir}/${artifact_library}" \
            | grep -Fxq '@rpath/libghostty-vt.dylib'; then
        echo "The staged libghostty-vt dylib has an unexpected install name." >&2
        exit 1
    fi
fi

catalog_sha="$(hash_file "${component_catalog}")"
library_sha="$(hash_file "${artifact_dir}/${artifact_library}")"
signature_removed_sha="${library_sha}"
if [[ "${target_rid}" == osx-* ]]; then
    # Apple's canonical signature removal retains program content while making
    # its identity independent of the later Developer-ID signature.
    signature_removed_sha="$("${dotnet}" run \
        --project "${repository_dir}/tools/GhostShell.Packaging/GhostShell.Packaging.csproj" \
        --configuration Release --verbosity quiet \
        ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
        -- native-signature-removed-sha256 "${artifact_dir}/${artifact_library}")"
    if [[ ! "${signature_removed_sha}" =~ ^[a-f0-9]{64}$ ]]; then
        echo "The native terminal content identity was not produced cleanly." >&2
        exit 1
    fi
fi
license_sha="$(hash_file "${artifact_dir}/GHOSTTY-LICENSE")"
zig_executable_sha="$(hash_file "${zig}")"
library_bytes="$(file_size "${artifact_dir}/${artifact_library}")"
license_bytes="$(file_size "${artifact_dir}/GHOSTTY-LICENSE")"
shell_integration_manifest_sha="$(hash_file "${shell_integration_manifest}")"
shell_integration_manifest_bytes="$(file_size "${shell_integration_manifest}")"
required_exports_sha="$(hash_file "${artifact_dir}/ghostty-vt-required-exports.txt")"
required_exports_bytes="$(file_size "${artifact_dir}/ghostty-vt-required-exports.txt")"
required_exports_count="$(wc -l < "${required_exports_manifest}" | tr -d '[:space:]')"
receipt="${artifact_dir}/native-terminal-build-receipt.json"
printf '%s\n' \
    '{' \
    '  "schemaVersion": 1,' \
    '  "format": "ghostshell-native-terminal-build-receipt-v1",' \
    '  "generator": "scripts/build-libghostty-vt.sh",' \
    "  \"catalogSha256\": \"${catalog_sha}\"," \
    "  \"targetRid\": \"${target_rid}\"," \
    "  \"pty\": ${pty_receipt}," \
    '  "source": {' \
    "    \"repository\": \"${ghostty_repository}\"," \
    "    \"commit\": \"${ghostty_commit}\"" \
    '  },' \
    '  "toolchain": {' \
    "    \"zigVersion\": \"${zig_version}\"," \
    "    \"zigDistribution\": \"${zig_distribution}\"," \
    "    \"zigArchiveSha256\": \"${zig_archive_sha}\"," \
    "    \"zigExecutableSha256\": \"${zig_executable_sha}\"" \
    '  },' \
    '  "build": {' \
    "    \"target\": \"${zig_target}\"," \
    "    \"libraryVersion\": \"${library_version}\"," \
    "    \"patchCount\": ${patch_count}," \
    "    \"patchSetSha256\": \"${patch_set_sha}\"," \
    "    \"testsTargetRid\": \"${host_rid}\"," \
    '    "testsPassed": true,' \
    '    "options": ["-Demit-lib-vt=true", "-Demit-xcframework=false", "-Doptimize=ReleaseFast"]' \
    '  },' \
    '  "abi": {' \
    "    \"ghostShellExtension\": ${ghostshell_extension_abi}," \
    "    \"ghostShellExtensionExport\": \"${ghostshell_extension_export}\"," \
    '    "requiredExportsPath": "ghostty-vt-required-exports.txt",' \
    "    \"requiredExportsCount\": ${required_exports_count}," \
    "    \"requiredExportsBytes\": ${required_exports_bytes}," \
    "    \"requiredExportsSha256\": \"${required_exports_sha}\"" \
    '  },' \
    '  "artifact": {' \
    "    \"path\": \"${artifact_library}\"," \
    "    \"bytes\": ${library_bytes}," \
    "    \"sha256\": \"${library_sha}\"," \
    "    \"signatureRemovedSha256\": \"${signature_removed_sha}\"" \
    '  },' \
    '  "license": {' \
    '    "path": "GHOSTTY-LICENSE",' \
    "    \"bytes\": ${license_bytes}," \
    "    \"sha256\": \"${license_sha}\"" \
    '  },' \
    '  "shellIntegration": {' \
    '    "directory": "ghostty/shell-integration",' \
    '    "manifestPath": "ghostty/shell-integration/MANIFEST.sha256",' \
    "    \"manifestBytes\": ${shell_integration_manifest_bytes}," \
    "    \"manifestSha256\": \"${shell_integration_manifest_sha}\"," \
    "    \"fileCount\": $((${#shell_integration_files[@]} + 1))" \
    '  }' \
    '}' > "${receipt}"

# Font shaping and presentation live above libghostty-vt, but must consume the
# same official JetBrains Mono package pinned by this exact Ghostty checkout.
# Publish the independently receipted common assets only after every native
# test and ABI gate above has passed.
"${script_dir}/build-terminal-font-assets.sh" --zig "${zig}"

"${dotnet}" run \
    --project "${repository_dir}/tools/GhostShell.Packaging/GhostShell.Packaging.csproj" \
    --configuration Release \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    -- \
    native-publish-artifacts \
    --component terminal \
    --staged-directory "${artifact_dir}" \
    --destination "${artifact_parent_dir}/${target_rid}"

echo "Published libghostty-vt ${ghostty_commit} for ${target_rid}."
