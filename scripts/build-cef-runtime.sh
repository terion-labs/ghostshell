#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
vendor_dir="${repository_dir}/vendor/exclr8cef"
catalog="${repository_dir}/licenses/cef-runtime-components.json"
patch_manifest="${vendor_dir}/ASURA-PATCHSET.sha256"
source_manifest="${vendor_dir}/ASURA-SOURCE-SNAPSHOT.sha256"
artifact_parent_dir="${repository_dir}/native/artifacts"
download_cache_dir="${repository_dir}/.deps/cef"
dotnet=""
target_rid=""
allow_unsandboxed_windows=false
cef_build_jobs="${ASURA_CEF_BUILD_JOBS:-4}"
dotnet_artifacts_arguments=()
if [[ -n "${ASURA_BUILD_ARTIFACTS_ROOT:-}" ]]; then
    dotnet_artifacts_arguments=(--artifacts-path "${ASURA_BUILD_ARTIFACTS_ROOT}")
fi

usage() {
    cat >&2 <<'EOF'
Usage: ./scripts/build-cef-runtime.sh --rid <runtime-identifier> [options]

Builds the pinned Asura Exclr8CEF source snapshot against the reviewed CEF
archive, creates and validates a complete runtime receipt, and atomically
publishes native/artifacts/<rid>/cef while preserving other RID artifacts.

Options:
  --rid <rid>        osx-arm64, osx-x64, linux-arm64, linux-x64, or win-x64
  --dotnet <path>    dotnet executable (defaults to .dotnet/dotnet, then PATH)
  --allow-unsandboxed-windows-development
                      Explicitly permit a non-production Windows build. CEF 150
                      production Windows remains blocked pending a native
                      bootstrap/CLR launcher.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            target_rid="$2"
            shift 2
            ;;
        --dotnet)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            dotnet="$2"
            shift 2
            ;;
        --allow-unsandboxed-windows-development)
            allow_unsandboxed_windows=true
            shift
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

if [[ -z "${target_rid}" ]]; then
    usage
    exit 64
fi
if [[ ! "${cef_build_jobs}" =~ ^[1-9][0-9]*$ ]]; then
    echo "ASURA_CEF_BUILD_JOBS must be a positive integer." >&2
    exit 64
fi

case "${target_rid}" in
    osx-arm64)
        cef_platform="macosarm64"
        target_os="Darwin"
        target_arch="arm64"
        shim_name="libexclr8cef.dylib"
        cmake_arch="arm64"
        ;;
    osx-x64)
        cef_platform="macosx64"
        target_os="Darwin"
        target_arch="x86_64"
        shim_name="libexclr8cef.dylib"
        cmake_arch="x86_64"
        ;;
    linux-arm64)
        cef_platform="linuxarm64"
        target_os="Linux"
        target_arch="aarch64"
        shim_name="libexclr8cef.so"
        cmake_arch=""
        ;;
    linux-x64)
        cef_platform="linux64"
        target_os="Linux"
        target_arch="x86_64"
        shim_name="libexclr8cef.so"
        cmake_arch=""
        ;;
    win-x64)
        cef_platform="windows64"
        target_os="Windows"
        target_arch="x86_64"
        shim_name="exclr8cef.dll"
        cmake_arch=""
        ;;
    *)
        echo "Unsupported target RID ${target_rid}." >&2
        exit 64
        ;;
esac

host_uname="$(uname -s)"
case "${host_uname}" in
    Darwin) host_os="Darwin" ;;
    Linux) host_os="Linux" ;;
    MINGW*|MSYS*|CYGWIN*) host_os="Windows" ;;
    *)
        echo "Unsupported CEF build host ${host_uname}." >&2
        exit 1
        ;;
esac
if [[ "${host_os}" != "${target_os}" ]]; then
    echo "CEF ${target_rid} must be built on ${target_os}; this host is ${host_os}." >&2
    exit 1
fi

if [[ -z "${dotnet}" ]]; then
    if [[ -x "${repository_dir}/.dotnet/dotnet" ]]; then
        dotnet="${repository_dir}/.dotnet/dotnet"
    elif command -v dotnet >/dev/null 2>&1; then
        dotnet="$(command -v dotnet)"
    else
        echo "A .NET SDK is required. Run scripts/bootstrap.sh or pass --dotnet." >&2
        exit 1
    fi
fi
if [[ ! -x "${dotnet}" ]]; then
    echo "The requested dotnet executable is unavailable: ${dotnet}." >&2
    exit 1
fi
for command_name in cmake curl file python3 tar; do
    if ! command -v "${command_name}" >/dev/null 2>&1; then
        echo "The CEF build requires ${command_name}." >&2
        exit 1
    fi
done
if [[ ! -f "${catalog}" || -L "${catalog}" ]]; then
    echo "The reviewed CEF runtime catalog is missing or linked." >&2
    exit 1
fi
if [[ ! -f "${patch_manifest}" || -L "${patch_manifest}" ]]; then
    echo "The reviewed Exclr8CEF patch manifest is missing or linked." >&2
    exit 1
fi
if [[ ! -f "${source_manifest}" || -L "${source_manifest}" ]]; then
    echo "The reviewed Exclr8CEF source manifest is missing or linked." >&2
    exit 1
fi
if [[ -L "${repository_dir}/native" || -L "${artifact_parent_dir}" ]]; then
    echo "The native artifact path must not contain a symbolic-link boundary." >&2
    exit 1
fi

manifest_record="$(python3 - \
    "${patch_manifest}" \
    "${source_manifest}" \
    "${vendor_dir}" <<'PY'
import hashlib
import pathlib
import re
import sys

patch_manifest = pathlib.Path(sys.argv[1])
source_manifest = pathlib.Path(sys.argv[2])
root_input = pathlib.Path(sys.argv[3])
if root_input.is_symlink():
    raise SystemExit("The Exclr8CEF source root must not be a symbolic link.")
root = root_input.resolve(strict=True)

def checked_file(relative: str, label: str) -> pathlib.Path:
    relative_path = pathlib.PurePosixPath(relative)
    if relative_path.is_absolute() or ".." in relative_path.parts:
        raise SystemExit(f"The Exclr8CEF {label} path is unsafe: {relative}")
    candidate = root
    for part in relative_path.parts:
        candidate = candidate / part
        if candidate.is_symlink():
            raise SystemExit(f"The Exclr8CEF {label} path is linked: {relative}")
    try:
        resolved = candidate.resolve(strict=True)
    except FileNotFoundError:
        raise SystemExit(f"The Exclr8CEF {label} path is missing: {relative}")
    if root not in resolved.parents or not resolved.is_file():
        raise SystemExit(f"The Exclr8CEF {label} path is unsafe: {relative}")
    return resolved

def verify_manifest(manifest: pathlib.Path, label: str):
    content = manifest.read_bytes()
    lines = content.decode("utf-8").splitlines()
    paths = []
    for line in lines:
        match = re.fullmatch(r"([0-9a-f]{64})  ([^\\]+)", line)
        if match is None:
            raise SystemExit(f"The Exclr8CEF {label} manifest is malformed.")
        expected, relative = match.groups()
        candidate = checked_file(relative, label)
        actual = hashlib.sha256(candidate.read_bytes()).hexdigest()
        if actual != expected:
            raise SystemExit(f"The Exclr8CEF {label} file changed: {relative}")
        paths.append(relative)
    if paths != sorted(set(paths)):
        raise SystemExit(
            f"The Exclr8CEF {label} manifest must be sorted and unique.")
    return content, paths

patch_content, _ = verify_manifest(patch_manifest, "patch")
source_content, source_paths = verify_manifest(source_manifest, "source")
excluded_directories = {".git", "third_party", "build", "bin", "obj"}
actual_paths = []
for candidate in root.rglob("*"):
    relative = candidate.relative_to(root)
    if any(part in excluded_directories for part in relative.parts):
        continue
    if relative.as_posix() == source_manifest.name:
        continue
    if candidate.is_symlink():
        raise SystemExit(
            f"The Exclr8CEF source tree contains a link: {relative.as_posix()}")
    if candidate.is_file():
        actual_paths.append(relative.as_posix())
if sorted(actual_paths) != source_paths:
    missing = sorted(set(source_paths) - set(actual_paths))
    unexpected = sorted(set(actual_paths) - set(source_paths))
    raise SystemExit(
        "The Exclr8CEF source closure changed. "
        f"Missing: {missing}. Unexpected: {unexpected}.")

print("\t".join([
    hashlib.sha256(patch_content).hexdigest(),
    hashlib.sha256(source_content).hexdigest(),
]))
PY
)"
IFS=$'\t' read -r patch_set_sha source_snapshot_sha <<< "${manifest_record}"

catalog_record="$(python3 - "${catalog}" "${target_rid}" <<'PY'
import json
import pathlib
import sys

catalog = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
rid = sys.argv[2]
distribution = next(
    (item for item in catalog["distributions"] if item["rid"] == rid),
    None,
)
if distribution is None:
    raise SystemExit(f"RID {rid} is not in the reviewed CEF catalog.")
print("\t".join([
    catalog["cefVersion"],
    catalog["bindingVersion"],
    catalog["bindingPatchSetSha256"],
    catalog["bindingSourceSnapshotSha256"],
    distribution["platform"],
    distribution["archiveSha1"],
    distribution["archiveSha256"],
]))
PY
)"
IFS=$'\t' read -r cef_version binding_version catalog_patch_sha catalog_source_sha catalog_platform archive_sha1 archive_sha256 <<< "${catalog_record}"
if [[ "${catalog_platform}" != "${cef_platform}" ]]; then
    echo "The CEF catalog platform does not match ${target_rid}." >&2
    exit 1
fi
if [[ "${catalog_patch_sha}" != "${patch_set_sha}" ]]; then
    echo "The vendored Exclr8CEF patch manifest does not match the reviewed catalog." >&2
    exit 1
fi
if [[ "${catalog_source_sha}" != "${source_snapshot_sha}" ]]; then
    echo "The vendored Exclr8CEF source manifest does not match the reviewed catalog." >&2
    exit 1
fi
if [[ "$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "${vendor_dir}/cef.json")" != "${cef_version}" ]]; then
    echo "The vendored CEF version does not match the reviewed catalog." >&2
    exit 1
fi

mkdir -p "${download_cache_dir}" "${artifact_parent_dir}"
archive_name="cef_binary_${cef_version}_${cef_platform}_minimal.tar.bz2"
archive_path="${download_cache_dir}/${archive_name}"
url_version="${cef_version//+/%2B}"
archive_url="https://cef-builds.spotifycdn.com/cef_binary_${url_version}_${cef_platform}_minimal.tar.bz2"
if [[ ! -f "${archive_path}" ]]; then
    download_path="$(mktemp "${download_cache_dir}/.cef-download.XXXXXX")"
    cleanup_download() { rm -f -- "${download_path}"; }
    trap cleanup_download EXIT
    echo "Downloading reviewed CEF ${cef_version} (${cef_platform})..."
    curl -fL --progress-bar -o "${download_path}" "${archive_url}"
    mv -- "${download_path}" "${archive_path}"
    trap - EXIT
fi

read -r actual_archive_sha1 actual_archive_sha256 <<< "$(python3 - "${archive_path}" <<'PY'
import hashlib
import pathlib
import sys

sha1 = hashlib.sha1()
sha256 = hashlib.sha256()
with pathlib.Path(sys.argv[1]).open("rb") as stream:
    for chunk in iter(lambda: stream.read(1024 * 1024), b""):
        sha1.update(chunk)
        sha256.update(chunk)
print(sha1.hexdigest(), sha256.hexdigest())
PY
)"
if [[ "${actual_archive_sha1}" != "${archive_sha1}" \
      || "${actual_archive_sha256}" != "${archive_sha256}" ]]; then
    echo "The CEF archive failed its reviewed SHA-1/SHA-256 checks." >&2
    exit 1
fi

build_run_dir="$(mktemp -d "${TMPDIR:-/tmp}/asura-cef-runtime.XXXXXX")"
artifact_staging_parent="$(mktemp -d "${artifact_parent_dir}/.asura-native-artifacts.XXXXXX")"
artifact_dir="${artifact_staging_parent}/${target_rid}"
cef_artifact_dir="${artifact_dir}/cef"
cleanup() {
    rm -rf -- "${build_run_dir}" "${artifact_staging_parent}"
}
trap cleanup EXIT
mkdir -p "${build_run_dir}/source" "${artifact_dir}" "${cef_artifact_dir}"

echo "Extracting the verified CEF archive..."
tar -xjf "${archive_path}" -C "${build_run_dir}/source"
cef_root="$(find "${build_run_dir}/source" -mindepth 1 -maxdepth 1 -type d -name 'cef_binary_*' -print -quit)"
if [[ -z "${cef_root}" || ! -f "${cef_root}/include/cef_version.h" ]]; then
    echo "The verified CEF archive has an unexpected layout." >&2
    exit 1
fi

cmake_options=(
    -S "${vendor_dir}/native"
    -B "${build_run_dir}/build"
    -DCMAKE_BUILD_TYPE=Release
    "-DEXCLR8CEF_CEF_ROOT=${cef_root}"
)
if [[ -n "${cmake_arch}" ]]; then
    cmake_options+=("-DCMAKE_OSX_ARCHITECTURES=${cmake_arch}")
fi
if [[ "${target_rid}" == "win-x64" \
      && "${allow_unsandboxed_windows}" == true ]]; then
    cmake_options+=(-DUSE_SANDBOX=OFF)
fi

echo "Building Exclr8CEF ${binding_version}..."
EXCLR8CEF_PLATFORM="${cef_platform}" cmake "${cmake_options[@]}"
cmake --build "${build_run_dir}/build" \
    --config Release \
    --target exclr8cef_version_probe exclr8cef_demo \
    --parallel "${cef_build_jobs}"

find_built_file() {
    local name="$1"
    local candidates=(
        "${build_run_dir}/build/shim/${name}"
        "${build_run_dir}/build/shim/Release/${name}"
        "${build_run_dir}/build/Release/${name}"
    )
    for candidate in "${candidates[@]}"; do
        if [[ -f "${candidate}" ]]; then
            printf '%s\n' "${candidate}"
            return
        fi
    done
    echo "The native build did not produce ${name} in a reviewed output location." >&2
    exit 1
}

find_built_helper() {
    local name="$1"
    local candidates=(
        "${build_run_dir}/build/demo/Release/${name}"
        "${build_run_dir}/build/demo/${name}"
        "${build_run_dir}/build/Release/${name}"
    )
    for candidate in "${candidates[@]}"; do
        if [[ -d "${candidate}" ]]; then
            printf '%s\n' "${candidate}"
            return
        fi
    done
    echo "The native build did not produce helper ${name} in a reviewed output location." >&2
    exit 1
}

shim_source="$(find_built_file "${shim_name}")"
cp -L "${shim_source}" "${cef_artifact_dir}/${shim_name}"
chmod 0755 "${cef_artifact_dir}/${shim_name}"
cp "${cef_root}/LICENSE.txt" "${cef_artifact_dir}/CEF-LICENSE.txt"
cp "${cef_root}/CREDITS.html" "${cef_artifact_dir}/CEF-CREDITS.html"
cp "${vendor_dir}/LICENSE" "${cef_artifact_dir}/EXCLR8CEF-LICENSE.txt"

if [[ "${target_os}" == "Darwin" ]]; then
    framework_name="Chromium Embedded Framework.framework"
    cp -RL "${cef_root}/Release/${framework_name}" \
        "${cef_artifact_dir}/${framework_name}"

    helper_suffixes=("" " (Alerts)" " (GPU)" " (Plugin)" " (Renderer)")
    helper_identifiers=("" ".alerts" ".gpu" ".plugin" ".renderer")
    for index in "${!helper_suffixes[@]}"; do
        source_name="exclr8cef_demo Helper${helper_suffixes[$index]}"
        target_name="Asura Helper${helper_suffixes[$index]}"
        source_bundle="$(find_built_helper "${source_name}.app")"
        target_bundle="${cef_artifact_dir}/${target_name}.app"
        cp -RL "${source_bundle}" "${target_bundle}"
        mv -- \
            "${target_bundle}/Contents/MacOS/${source_name}" \
            "${target_bundle}/Contents/MacOS/${target_name}"
        chmod 0755 "${target_bundle}/Contents/MacOS/${target_name}"
        python3 - \
            "${target_bundle}/Contents/Info.plist" \
            "${target_name}" \
            "sh.asura.helper${helper_identifiers[$index]}" <<'PY'
import pathlib
import plistlib
import sys

path = pathlib.Path(sys.argv[1])
with path.open("rb") as stream:
    values = plistlib.load(stream)
for key in ("CFBundleDisplayName", "CFBundleExecutable", "CFBundleName"):
    values[key] = sys.argv[2]
values["CFBundleIdentifier"] = sys.argv[3]
with path.open("wb") as stream:
    plistlib.dump(values, stream, fmt=plistlib.FMT_XML, sort_keys=True)
PY
    done
elif [[ "${target_os}" == "Windows" ]]; then
    windows_binaries=(
        chrome_elf.dll d3dcompiler_47.dll dxcompiler.dll dxil.dll libcef.dll
        libEGL.dll libGLESv2.dll v8_context_snapshot.bin vk_swiftshader.dll
        vk_swiftshader_icd.json vulkan-1.dll
    )
    for name in "${windows_binaries[@]}"; do
        cp "${cef_root}/Release/${name}" "${cef_artifact_dir}/${name}"
    done
    cp "${cef_root}/Resources/chrome_100_percent.pak" "${cef_artifact_dir}/"
    cp "${cef_root}/Resources/chrome_200_percent.pak" "${cef_artifact_dir}/"
    cp "${cef_root}/Resources/resources.pak" "${cef_artifact_dir}/"
    cp "${cef_root}/Resources/icudtl.dat" "${cef_artifact_dir}/"
    cp -R "${cef_root}/Resources/locales" "${cef_artifact_dir}/locales"
else
    linux_binaries=(
        chrome-sandbox libcef.so libEGL.so libGLESv2.so libvk_swiftshader.so
        libvulkan.so.1 v8_context_snapshot.bin vk_swiftshader_icd.json
    )
    for name in "${linux_binaries[@]}"; do
        cp "${cef_root}/Release/${name}" "${cef_artifact_dir}/${name}"
    done
    chmod 0755 "${cef_artifact_dir}/chrome-sandbox"
    cp "${cef_root}/Resources/chrome_100_percent.pak" "${cef_artifact_dir}/"
    cp "${cef_root}/Resources/chrome_200_percent.pak" "${cef_artifact_dir}/"
    cp "${cef_root}/Resources/resources.pak" "${cef_artifact_dir}/"
    cp "${cef_root}/Resources/icudtl.dat" "${cef_artifact_dir}/"
    cp -R "${cef_root}/Resources/locales" "${cef_artifact_dir}/locales"
fi

require_architecture() {
    local binary="$1"
    if [[ "${target_os}" == "Darwin" ]]; then
        local architectures
        architectures="$(lipo -archs "${binary}")"
        if [[ "${architectures}" != "${target_arch}" ]]; then
            echo "Wrong architecture for ${binary}: expected ${target_arch}, found ${architectures}." >&2
            exit 1
        fi
        return
    fi

    local description
    description="$(file -b "${binary}")"
    case "${target_rid}" in
        linux-x64)
            [[ "${description}" == *"ELF 64-bit"* && "${description}" == *"x86-64"* ]] || {
                echo "Wrong architecture for ${binary}: ${description}." >&2; exit 1; }
            ;;
        linux-arm64)
            [[ "${description}" == *"ELF 64-bit"* && "${description}" == *"ARM aarch64"* ]] || {
                echo "Wrong architecture for ${binary}: ${description}." >&2; exit 1; }
            ;;
        win-x64)
            [[ "${description}" == *"PE32+"* && "${description}" == *"x86-64"* ]] || {
                echo "Wrong architecture for ${binary}: ${description}." >&2; exit 1; }
            ;;
    esac
}

require_architecture "${cef_artifact_dir}/${shim_name}"
if [[ "${target_os}" == "Darwin" ]]; then
    require_architecture "${cef_artifact_dir}/${framework_name}/${framework_name%.framework}"
    for suffix in "${helper_suffixes[@]}"; do
        helper_name="Asura Helper${suffix}"
        require_architecture \
            "${cef_artifact_dir}/${helper_name}.app/Contents/MacOS/${helper_name}"
    done
elif [[ "${target_os}" == "Windows" ]]; then
    require_architecture "${cef_artifact_dir}/libcef.dll"
else
    require_architecture "${cef_artifact_dir}/libcef.so"
fi

host_arch="$(uname -m)"
host_rid=""
case "${host_os}:${host_arch}" in
    Darwin:arm64) host_rid="osx-arm64" ;;
    Darwin:x86_64) host_rid="osx-x64" ;;
    Linux:aarch64|Linux:arm64) host_rid="linux-arm64" ;;
    Linux:x86_64) host_rid="linux-x64" ;;
    Windows:x86_64|Windows:amd64) host_rid="win-x64" ;;
esac
if [[ "${target_rid}" == "${host_rid}" ]]; then
    version_probe="$(find_built_file 'exclr8cef_version_probe')"
    version_output="$("${version_probe}")"
    [[ "${version_output}" == *"${binding_version}"* \
        && "${version_output}" == *"150.0.9"* \
        && "${version_output}" == *"150.0.7871.46"* ]] || {
        echo "The Exclr8CEF native version probe did not report the reviewed identity." >&2
        exit 1
    }

    if [[ "${target_os}" == "Darwin" ]]; then
        set +e
        "${cef_artifact_dir}/Asura Helper.app/Contents/MacOS/Asura Helper"
        helper_exit=$?
        set -e
        if [[ ${helper_exit} -ne 255 ]]; then
            echo "The macOS CEF helper did not return the main-process sentinel." >&2
            exit 1
        fi
    fi
fi

"${dotnet}" run \
    --project "${repository_dir}/tools/Asura.Packaging/Asura.Packaging.csproj" \
    --configuration Release \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    -- \
    cef-runtime-receipt \
    --runtime-root "${cef_artifact_dir}" \
    --catalog "${catalog}" \
    --runtime-identifier "${target_rid}" \
    --archive-sha1 "${actual_archive_sha1}" \
    --archive-sha256 "${actual_archive_sha256}" \
    --patch-set-sha256 "${patch_set_sha}" \
    --source-snapshot-sha256 "${source_snapshot_sha}" \
    --output "${cef_artifact_dir}/cef-runtime-build-receipt.json"

"${dotnet}" run \
    --project "${repository_dir}/tools/Asura.Packaging/Asura.Packaging.csproj" \
    --configuration Release \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    -- \
    cef-runtime-validate \
    --runtime-root "${cef_artifact_dir}" \
    --catalog "${catalog}" \
    --runtime-identifier "${target_rid}"

if [[ "${target_rid}" == osx-arm64 && "${host_rid}" == osx-arm64 ]]; then
    bash "${script_dir}/test-browser-session-cookies.sh" "${cef_artifact_dir}"
    bash "${script_dir}/test-browser-authentication-origin.sh" "${cef_artifact_dir}"
    bash "${script_dir}/test-browser-hosted-popups.sh" "${cef_artifact_dir}"
fi

existing_artifact_dir="${artifact_parent_dir}/${target_rid}"

"${dotnet}" run \
    --project "${repository_dir}/tools/Asura.Packaging/Asura.Packaging.csproj" \
    --configuration Release \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    -- \
    native-publish-artifacts \
    --component cef \
    --staged-directory "${artifact_dir}" \
    --destination "${existing_artifact_dir}"

echo "Published verified CEF ${cef_version} runtime for ${target_rid}."
