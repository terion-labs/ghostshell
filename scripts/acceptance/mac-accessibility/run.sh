#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "mac-accessibility acceptance can run only on macOS." >&2
    exit 2
fi

build_dir="$(mktemp -d "${TMPDIR:-/tmp}/asura-mac-accessibility.XXXXXX")"
cleanup() {
    rm -rf -- "${build_dir}"
}
trap cleanup EXIT

xcrun swiftc \
    -parse-as-library \
    -warnings-as-errors \
    "${script_dir}/AsuraAccessibilityProbe.swift" \
    -o "${build_dir}/asura-mac-accessibility"

receipt_path="${build_dir}/receipt.json"
set +e
"${build_dir}/asura-mac-accessibility" >"${receipt_path}"
probe_exit_code=$?
set -e

python3 "${script_dir}/validate_receipt.py" "${receipt_path}"
cat "${receipt_path}"
exit "${probe_exit_code}"
