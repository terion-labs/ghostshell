#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
    echo "Usage: $0 <path-to-macos-executable>" >&2
    exit 64
fi

executable="$1"
# Keep the Mach-O identity aligned with the development/release bundle. Without
# an explicit identifier, ad-hoc signing invents a content hash and macOS cannot
# associate UserNotifications authorization with sh.asura consistently.
signing_identifier="sh.asura"
if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "The SDK declaration helper can only run on macOS." >&2
    exit 1
fi
if [[ ! -f "${executable}" || ! -x "${executable}" ]]; then
    echo "The macOS executable is missing or is not executable: ${executable}." >&2
    exit 1
fi
for required_tool in /usr/bin/vtool /usr/bin/codesign; do
    if [[ ! -x "${required_tool}" ]]; then
        echo "The macOS SDK declaration tool is unavailable: ${required_tool}." >&2
        exit 1
    fi
done

build_version="$(
    /usr/bin/vtool -show-build "${executable}" \
        | awk '
            $1 == "minos" { minimum = $2 }
            $1 == "sdk" { sdk = $2 }
            END {
                if (minimum == "" || sdk == "") exit 1
                print minimum " " sdk
            }'
)"
read -r minimum_macos declared_sdk <<<"${build_version}"

if [[ "${declared_sdk}" == "26.0" ]]; then
    /usr/bin/codesign \
        --force \
        --sign - \
        --identifier "${signing_identifier}" \
        "${executable}"
    /usr/bin/codesign --verify --strict "${executable}"
    exit 0
fi

rewritten="$(mktemp "$(dirname "${executable}")/.Asura.sdk26.XXXXXX")"
vtool_diagnostics="${rewritten}.vtool.log"
cleanup() {
    rm -f -- "${rewritten}" "${vtool_diagnostics}"
}
trap cleanup EXIT

if ! /usr/bin/vtool \
        -set-build-version macos "${minimum_macos}" 26.0 \
        -replace \
        -output "${rewritten}" \
        "${executable}" \
        2>"${vtool_diagnostics}"; then
    cat "${vtool_diagnostics}" >&2
    exit 1
fi
/bin/chmod +x "${rewritten}"
/bin/mv "${rewritten}" "${executable}"
/usr/bin/codesign \
    --force \
    --sign - \
    --identifier "${signing_identifier}" \
    "${executable}"

declared_sdk="$(
    /usr/bin/vtool -show-build "${executable}" \
        | awk '$1 == "sdk" { print $2; exit }'
)"
if [[ "${declared_sdk}" != "26.0" ]]; then
    echo "The executable does not declare macOS SDK 26.0: ${executable}." >&2
    exit 1
fi
/usr/bin/codesign --verify --strict "${executable}"
