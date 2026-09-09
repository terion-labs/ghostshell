#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

python3 "${script_dir}/validate_receipt.py" --self-test
python3 \
    "${script_dir}/verify_source_contract.py" \
    "${script_dir}/AsuraAccessibilityProbe.swift"

if [[ "$(uname -s)" == "Darwin" ]]; then
    build_dir="$(mktemp -d "${TMPDIR:-/tmp}/asura-mac-accessibility-test.XXXXXX")"
    cleanup() {
        rm -rf -- "${build_dir}"
    }
    trap cleanup EXIT

    xcrun swiftc \
        -parse-as-library \
        -warnings-as-errors \
        "${script_dir}/AsuraAccessibilityProbe.swift" \
        -o "${build_dir}/asura-mac-accessibility"
fi

echo "mac-accessibility probe checks passed"
