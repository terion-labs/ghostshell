#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
icon_document="${repository_dir}/assets/macos/Asura.icon"
output_icon="${repository_dir}/assets/macos/Asura.icns"
icon_composer_tool="${ICON_COMPOSER_TOOL:-/Applications/Icon Composer.app/Contents/Executables/ictool}"

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "The macOS icon build requires macOS." >&2
    exit 1
fi
if [[ ! -x "${icon_composer_tool}" ]]; then
    echo "Install Icon Composer or set ICON_COMPOSER_TOOL to ictool." >&2
    exit 1
fi
if [[ ! -d "${icon_document}" || -L "${icon_document}" ]]; then
    echo "The layered Icon Composer document is missing or linked." >&2
    exit 1
fi
if [[ ! -x /usr/bin/iconutil || ! -x /usr/bin/sips ]]; then
    echo "The macOS icon utilities are unavailable." >&2
    exit 1
fi

working_directory="$(mktemp -d "${TMPDIR:-/tmp}/asura-macos-icon.XXXXXX")"
cleanup() {
    rm -rf -- "${working_directory}"
}
trap cleanup EXIT

iconset="${working_directory}/Asura.iconset"
mkdir -- "${iconset}"

render() {
    local points="$1"
    local scale="$2"
    local suffix=""
    local expected_pixels=$((points * scale))
    if [[ "${scale}" == "2" ]]; then
        suffix="@2x"
    fi

    local output="${iconset}/icon_${points}x${points}${suffix}.png"
    "${icon_composer_tool}" \
        "${icon_document}" \
        --export-image \
        --output-file "${output}" \
        --platform macOS \
        --rendition Default \
        --width "${points}" \
        --height "${points}" \
        --scale "${scale}" \
        --design-generation 26

    local width
    local height
    width="$(/usr/bin/sips -g pixelWidth "${output}" | /usr/bin/awk '/pixelWidth:/ {print $2}')"
    height="$(/usr/bin/sips -g pixelHeight "${output}" | /usr/bin/awk '/pixelHeight:/ {print $2}')"
    if [[ "${width}" != "${expected_pixels}" || "${height}" != "${expected_pixels}" ]]; then
        echo "Icon Composer produced an unexpected ${width}x${height} rendition." >&2
        exit 1
    fi
}

for points in 16 32 128 256 512; do
    render "${points}" 1
    render "${points}" 2
done

candidate="${working_directory}/Asura.icns"
/usr/bin/iconutil --convert icns --output "${candidate}" "${iconset}"
if [[ "$(LC_ALL=C /usr/bin/head -c 4 "${candidate}")" != "icns" ]]; then
    echo "iconutil did not produce an ICNS container." >&2
    exit 1
fi

mkdir -p -- "$(dirname -- "${output_icon}")"
mv -- "${candidate}" "${output_icon}"
echo "Created ${output_icon} from ${icon_document}."
