#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
icon_document="${repository_dir}/assets/macos/Asura.icon"
output_directory=""
minimum_macos="13.0"

usage() {
    cat >&2 <<'EOF'
Usage: ./scripts/compile-macos-app-icon.sh --output <empty-directory>

Compiles the reviewed Asura.icon document with Xcode 26 actool. The output
is Assets.car plus inspection evidence used only during package assembly.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --output)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            output_directory="$2"
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

if [[ -z "${output_directory}" ]]; then
    usage
    exit 64
fi
if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "The macOS adaptive icon requires a macOS host with full Xcode 26." >&2
    exit 1
fi
if [[ ! -d "${icon_document}" || -L "${icon_document}" ]]; then
    echo "The reviewed Icon Composer document is missing or linked." >&2
    exit 1
fi
if find "${icon_document}" -type l -print -quit | /usr/bin/grep -q .; then
    echo "The reviewed Icon Composer document contains a symbolic link." >&2
    exit 1
fi
if [[ ! -d "${output_directory}" || -L "${output_directory}" ]]; then
    echo "The adaptive icon output must be an existing, unlinked directory." >&2
    exit 1
fi
if find "${output_directory}" -mindepth 1 -print -quit | /usr/bin/grep -q .; then
    echo "The adaptive icon output directory must be empty." >&2
    exit 1
fi

developer_directory="${DEVELOPER_DIR:-$(/usr/bin/xcode-select -p)}"
if [[ ! -d "${developer_directory}/Platforms/MacOSX.platform" ]]; then
    echo "Full Xcode is required; CommandLineTools cannot compile Asura.icon." >&2
    exit 1
fi

actool="$(DEVELOPER_DIR="${developer_directory}" /usr/bin/xcrun --sdk macosx --find actool)"
if [[ ! -x "${actool}" ]]; then
    echo "Full Xcode did not provide an executable actool." >&2
    exit 1
fi
actool_version_output="$("${actool}" --version --output-format=human-readable-text)"
actool_version="$({
    printf '%s\n' "${actool_version_output}" \
        | /usr/bin/awk -F ': ' '$1 == "short-bundle-version" { print $2 }'
} | /usr/bin/tail -n 1)"
if [[ ! "${actool_version}" =~ ^([0-9]+)(\.[0-9]+)*$ \
    || "${BASH_REMATCH[1]}" -lt 26 ]]; then
    echo "Asura.icon requires Xcode actool 26 or newer; found '${actool_version:-unknown}'." >&2
    exit 1
fi

partial_plist="${output_directory}/assetcatalog-generated-info.plist"
asset_info="${output_directory}/Assets.info.json"
DEVELOPER_DIR="${developer_directory}" "${actool}" \
    "${icon_document}" \
    --compile "${output_directory}" \
    --output-format human-readable-text \
    --notices \
    --warnings \
    --errors \
    --output-partial-info-plist "${partial_plist}" \
    --app-icon Asura \
    --include-all-app-icons \
    --enable-on-demand-resources NO \
    --development-region en \
    --target-device mac \
    --minimum-deployment-target "${minimum_macos}" \
    --platform macosx

asset_catalog="${output_directory}/Assets.car"
if [[ ! -f "${asset_catalog}" || -L "${asset_catalog}" ]]; then
    echo "Xcode actool did not produce a regular Assets.car." >&2
    exit 1
fi
if [[ ! -f "${partial_plist}" || -L "${partial_plist}" ]]; then
    echo "Xcode actool did not produce its partial Info.plist." >&2
    exit 1
fi
if [[ "$(/usr/bin/plutil -extract CFBundleIconName raw -o - "${partial_plist}")" != "Asura" ]]; then
    echo "Xcode actool did not declare Asura as the primary application icon." >&2
    exit 1
fi

/usr/bin/assetutil --info "${asset_catalog}" > "${asset_info}"
if ! /usr/bin/grep -Fq '"AssetType" : "Icon Image"' "${asset_info}" \
    || ! /usr/bin/grep -Fq '"Name" : "Asura"' "${asset_info}"; then
    echo "Assets.car does not contain the named Asura icon image." >&2
    exit 1
fi

echo "Compiled ${icon_document} with actool ${actool_version}."
