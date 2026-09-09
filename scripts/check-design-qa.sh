#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
if [[ -n "${ASURA_DESIGN_QA_CAPTURE_DIR:-}" ]]; then
    capture_dir="${ASURA_DESIGN_QA_CAPTURE_DIR}"
    mkdir -p "${capture_dir}"
    preserve_captures=1
else
    capture_dir="$(mktemp -d "${TMPDIR:-/tmp}/asura-design-qa.XXXXXX")"
    preserve_captures=0
fi

cleanup() {
    status=$?
    if [[ "${status}" == "0" && "${preserve_captures}" == "0" ]]; then
        rm -rf "${capture_dir}"
    elif [[ "${status}" != "0" ]]; then
        echo "Failed design QA captures preserved at ${capture_dir}" >&2
    fi
}
trap cleanup EXIT

if [[ -n "${ASURA_DOTNET:-}" ]]; then
    dotnet="${ASURA_DOTNET}"
else
    dotnet="${repository_dir}/.dotnet/dotnet"
fi

cd "${repository_dir}"
"${dotnet}" run \
    --project tools/Asura.DesignQa \
    --configuration Release \
    --no-build \
    --no-restore \
    -- \
    --gate \
    "${capture_dir}" \
    "${repository_dir}/tools/Asura.DesignQa/design-qa-baseline.json"
