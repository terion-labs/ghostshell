#!/usr/bin/env bash
set -euo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
dotnet="${ASURA_DOTNET:-${repository_dir}/.dotnet/dotnet}"
work="${1:-$(mktemp -d "${TMPDIR:-/tmp}/asura-browser-storage-aot.XXXXXX")}"
mkdir -p "${work}"
export SDKROOT="$(xcrun --sdk macosx --show-sdk-path)"
"${dotnet}" publish "${script_dir}/acceptance/browser-storage/Asura.BrowserStorageAcceptance.csproj" \
    --configuration Release --runtime osx-arm64 \
    --artifacts-path "${work}/artifacts" --output "${work}/publish" \
    -p:RestoreLockedMode=true -p:AsuraNativeAotLinker="${ASURA_NATIVE_AOT_LINKER:-}"
probe="${work}/publish/Asura.BrowserStorageAcceptance"
for scenario in restart repaired; do
    fixture="${work}/${scenario}"
    mkdir -m 700 "${fixture}"
    "${probe}" write "${fixture}"
    if [[ "${scenario}" == repaired ]]; then
        "${probe}" strip "${fixture}"
    fi
    "${probe}" read "${fixture}"
done
