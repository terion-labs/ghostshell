#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
dependencies_dir="${repository_dir}/.deps"
source_dir="${dependencies_dir}/ghostty-vt"
artifact_parent_dir="${repository_dir}/native/artifacts"
component_catalog="${repository_dir}/licenses/terminal-font-assets.json"
dotnet="${ASURA_DOTNET:-${repository_dir}/.dotnet/dotnet}"
dotnet_artifacts_arguments=()
if [[ -n "${ASURA_BUILD_ARTIFACTS_ROOT:-}" ]]; then
    dotnet_artifacts_arguments=(--artifacts-path "${ASURA_BUILD_ARTIFACTS_ROOT}")
fi

ghostty_repository="https://github.com/ghostty-org/ghostty.git"
ghostty_commit="08f039fbb3dea9c6b1cdb5ff4550666598122346"
dependency_name="JetBrains Mono"
dependency_version="2.304"
dependency_url="https://deps.files.ghostty.org/JetBrainsMono-2.304.tar.gz"
dependency_hash="N-V-__8AAIC5lwAVPJJzxnCAahSvZTIlG-HhtOvnM1uh-66x"
dependency_license="OFL-1.1"
zig_version="0.16.0"
zig=""

asset_files=(
    "JetBrainsMono-Bold.ttf"
    "JetBrainsMono-BoldItalic.ttf"
    "JetBrainsMono-Italic.ttf"
    "JetBrainsMono-Regular.ttf"
)
asset_source_paths=(
    "fonts/ttf/JetBrainsMono-Bold.ttf"
    "fonts/ttf/JetBrainsMono-BoldItalic.ttf"
    "fonts/ttf/JetBrainsMono-Italic.ttf"
    "fonts/ttf/JetBrainsMono-Regular.ttf"
)
asset_styles=("normal" "italic" "italic" "normal")
asset_weights=(700 700 400 400)
asset_bytes=(277828 279832 276840 273900)
asset_hashes=(
    "5590990c82e097397517f275f430af4546e1c45cff408bde4255dad142479dcb"
    "4039d5ce0ed225bf9c8b2c8c6436290ae2f356b7e90d70fa666227238324aa3b"
    "9d0a1f7a708e6af183f1193b7e81d40da294f5c67682c085d8401c60aac8ded4"
    "a0bf60ef0f83c5ed4d7a75d45838548b1f6873372dfac88f71804491898d138f"
)
license_file="OFL.txt"
license_bytes=4399
license_hash="30f0c136e3c88e422d0791acd97238870f9054a9729bc34cf2ff0d4ed8cac4ad"

usage() {
    cat >&2 <<'EOF'
Usage: ./scripts/build-terminal-font-assets.sh [--zig <path/to/zig>]

Fetches the official JetBrains Mono package pinned by Ghostty, verifies the
reviewed regular/bold/italic/bold-italic faces and OFL byte-for-byte, and
atomically publishes them under native/artifacts/common.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --zig)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            zig="$2"
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
if [[ -z "${zig}" ]]; then
    case "${host_os}:${host_arch}" in
        Darwin:arm64)
            zig_distribution="aarch64-macos"
            ;;
        Darwin:x86_64)
            zig_distribution="x86_64-macos"
            ;;
        Linux:aarch64|Linux:arm64)
            zig_distribution="aarch64-linux"
            ;;
        Linux:x86_64)
            zig_distribution="x86_64-linux"
            ;;
        *)
            echo "Unsupported font-asset build host ${host_os} ${host_arch}." >&2
            exit 1
            ;;
    esac
    zig="${dependencies_dir}/toolchains/zig-${zig_distribution}-${zig_version}/zig"
fi

zig_parent="$(dirname -- "${zig}")"
if [[ ! -d "${zig_parent}" || -L "${zig_parent}" ]]; then
    echo "The pinned Zig directory is unavailable or linked." >&2
    exit 1
fi
zig="$(cd -- "${zig_parent}" && pwd -P)/$(basename -- "${zig}")"

if [[ ! -x "${dotnet}" ]]; then
    echo "Run ./scripts/bootstrap.sh before building terminal font assets." >&2
    exit 1
fi
if [[ ! -x "${zig}" || "$("${zig}" version)" != "${zig_version}" ]]; then
    echo "The terminal font build requires the pinned Zig ${zig_version} executable." >&2
    exit 1
fi
if [[ ! -f "${component_catalog}" || -L "${component_catalog}" ]]; then
    echo "The reviewed terminal font component catalog is missing or linked." >&2
    exit 1
fi
if [[ ! -d "${source_dir}/.git" || -L "${source_dir}" ]]; then
    echo "The pinned Ghostty source checkout is unavailable or linked." >&2
    exit 1
fi
if [[ "$(git -C "${source_dir}" rev-parse HEAD)" != "${ghostty_commit}" ]]; then
    echo "The Ghostty checkout does not match the terminal font source pin." >&2
    exit 1
fi
if [[ -n "$(git -C "${source_dir}" status --porcelain --untracked-files=all)" ]]; then
    echo "The pinned Ghostty checkout contains local changes." >&2
    exit 1
fi
if [[ ! -f "${source_dir}/build.zig.zon" || -L "${source_dir}/build.zig.zon" ]]; then
    echo "The pinned Ghostty dependency manifest is missing or linked." >&2
    exit 1
fi
if [[ "$(grep -Fc ".url = \"${dependency_url}\"" "${source_dir}/build.zig.zon")" != "1" \
      || "$(grep -Fc ".hash = \"${dependency_hash}\"" "${source_dir}/build.zig.zon")" != "1" ]]; then
    echo "Ghostty's JetBrains Mono dependency declaration no longer matches the reviewed pin." >&2
    exit 1
fi
if [[ -L "${repository_dir}/native" || -L "${artifact_parent_dir}" ]]; then
    echo "The common artifact path must not contain a symbolic-link boundary." >&2
    exit 1
fi
mkdir -p "${dependencies_dir}/zig-font-global-cache" "${artifact_parent_dir}"

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

fetch_output="$({
    cd -- "${source_dir}"
    "${zig}" fetch \
        --global-cache-dir "${dependencies_dir}/zig-font-global-cache" \
        "${dependency_url}"
} | tr -d '\r')"
if [[ "${fetch_output}" != "${dependency_hash}" ]]; then
    echo "The fetched JetBrains Mono package did not produce its pinned Zig hash." >&2
    exit 1
fi

# Zig 0.16 materializes explicitly fetched packages in the checkout's ignored
# zig-pkg directory. The package hash and every staged file are independently
# verified below; no unchecked content from the archive is published.
package_dir="${source_dir}/zig-pkg/${dependency_hash}"
if [[ ! -d "${package_dir}" || -L "${package_dir}" ]]; then
    echo "The pinned JetBrains Mono package directory is unavailable or linked." >&2
    exit 1
fi

for index in "${!asset_files[@]}"; do
    source_path="${package_dir}/${asset_source_paths[$index]}"
    if [[ ! -f "${source_path}" || -L "${source_path}" ]]; then
        echo "Pinned terminal font is missing or linked: ${asset_files[$index]}." >&2
        exit 1
    fi
    if [[ "$(file_size "${source_path}")" != "${asset_bytes[$index]}" \
          || "$(hash_file "${source_path}")" != "${asset_hashes[$index]}" ]]; then
        echo "Pinned terminal font failed its reviewed size or SHA-256 check: ${asset_files[$index]}." >&2
        exit 1
    fi
    font_magic="$(od -An -tx1 -N4 "${source_path}" | tr -d '[:space:]')"
    if [[ "${font_magic}" != "00010000" ]]; then
        echo "Pinned terminal font has an unexpected TrueType header: ${asset_files[$index]}." >&2
        exit 1
    fi
done

license_source="${package_dir}/${license_file}"
if [[ ! -f "${license_source}" || -L "${license_source}" \
      || "$(file_size "${license_source}")" != "${license_bytes}" \
      || "$(hash_file "${license_source}")" != "${license_hash}" ]]; then
    echo "The pinned JetBrains Mono OFL failed its reviewed size or SHA-256 check." >&2
    exit 1
fi

artifact_staging_parent="$(mktemp -d "${artifact_parent_dir}/.asura-native-artifacts.XXXXXX")"
artifact_dir="${artifact_staging_parent}/common"
font_dir="${artifact_dir}/fonts/JetBrainsMono"
cleanup() {
    rm -rf -- "${artifact_staging_parent}"
}
trap cleanup EXIT
mkdir -p "${font_dir}"

manifest="${font_dir}/MANIFEST.sha256"
: > "${manifest}"
for index in "${!asset_files[@]}"; do
    cp "${package_dir}/${asset_source_paths[$index]}" \
        "${font_dir}/${asset_files[$index]}"
    printf '%s  %s\n' \
        "${asset_hashes[$index]}" \
        "${asset_files[$index]}" >> "${manifest}"
done
cp "${license_source}" "${font_dir}/${license_file}"
printf '%s  %s\n' "${license_hash}" "${license_file}" >> "${manifest}"

catalog_sha="$(hash_file "${component_catalog}")"
manifest_sha="$(hash_file "${manifest}")"
manifest_bytes="$(file_size "${manifest}")"
receipt="${artifact_dir}/terminal-font-assets-build-receipt.json"
{
    printf '%s\n' \
        '{' \
        '  "schemaVersion": 1,' \
        '  "format": "asura-terminal-font-assets-build-receipt-v1",' \
        '  "generator": "scripts/build-terminal-font-assets.sh",' \
        "  \"catalogSha256\": \"${catalog_sha}\"," \
        '  "source": {' \
        "    \"repository\": \"${ghostty_repository}\"," \
        "    \"commit\": \"${ghostty_commit}\"" \
        '  },' \
        '  "dependency": {' \
        "    \"name\": \"${dependency_name}\"," \
        "    \"version\": \"${dependency_version}\"," \
        "    \"url\": \"${dependency_url}\"," \
        "    \"zigPackageHash\": \"${dependency_hash}\"," \
        "    \"license\": \"${dependency_license}\"" \
        '  },' \
        '  "directory": "fonts/JetBrainsMono",' \
        '  "manifest": {' \
        '    "path": "fonts/JetBrainsMono/MANIFEST.sha256",' \
        '    "fileCount": 5,' \
        "    \"bytes\": ${manifest_bytes}," \
        "    \"sha256\": \"${manifest_sha}\"" \
        '  },' \
        '  "assets": ['
    for index in "${!asset_files[@]}"; do
        if [[ "${index}" -lt $((${#asset_files[@]} - 1)) ]]; then
            suffix=','
        else
            suffix=''
        fi
        printf '%s\n' \
            '    {' \
            "      \"file\": \"${asset_files[$index]}\"," \
            "      \"sourcePath\": \"${asset_source_paths[$index]}\"," \
            "      \"style\": \"${asset_styles[$index]}\"," \
            "      \"weight\": ${asset_weights[$index]}," \
            "      \"bytes\": ${asset_bytes[$index]}," \
            "      \"sha256\": \"${asset_hashes[$index]}\"" \
            "    }${suffix}"
    done
    printf '%s\n' \
        '  ],' \
        '  "license": {' \
        '    "path": "fonts/JetBrainsMono/OFL.txt",' \
        "    \"bytes\": ${license_bytes}," \
        "    \"sha256\": \"${license_hash}\"" \
        '  }' \
        '}'
} > "${receipt}"

"${dotnet}" run \
    --project "${repository_dir}/tools/Asura.Packaging/Asura.Packaging.csproj" \
    --configuration Release \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    -- \
    native-publish-artifacts \
    --staged-directory "${artifact_dir}" \
    --destination "${artifact_parent_dir}/common"

echo "Published JetBrains Mono ${dependency_version} terminal font assets."
