#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
build_dir="${repository_dir}/native/artifacts/workspace-backend-build"
distribution="${build_dir}/distribution"
project="${repository_dir}/src/GhostShell.Backend/GhostShell.Backend.csproj"
dotnet="${GHOSTSHELL_DOTNET:-${repository_dir}/.dotnet/dotnet}"
packager="${script_dir}/package-workspace-backend.py"
architecture="${GHOSTSHELL_BACKEND_ARCH:-arm64}"
[[ "${architecture}" == arm64 || "${architecture}" == x64 ]] || { echo "Unsupported backend architecture." >&2; exit 64; }
if [[ "${architecture}" == x64 ]]; then
    distribution="${distribution}/x64"
fi
export GHOSTSHELL_BACKEND_ARCH="${architecture}"

case "${1:-}" in
    --verify)
        python3 "${packager}" verify "${repository_dir}" "${distribution}"
        exit 0 ;;
    --help|-h)
        echo "Usage: ./scripts/build-workspace-backend.sh [--verify]"
        exit 0 ;;
    "") ;;
    *) echo "Unknown workspace backend build option." >&2; exit 64 ;;
esac
[[ $# -eq 0 ]] || { echo "Unexpected backend build arguments." >&2; exit 64; }
[[ -x "${dotnet}" ]] || { echo "Install the repository-pinned .NET SDK first." >&2; exit 1; }
expected_sdk="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["sdk"]["version"])' "${repository_dir}/global.json")"
[[ "$("${dotnet}" --version)" == "${expected_sdk}" ]] || { echo "The backend requires the repository-pinned SDK." >&2; exit 1; }
export NUGET_PACKAGES="${NUGET_PACKAGES:-${repository_dir}/.nuget/packages}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
mkdir -p "${build_dir}"
staging="$(mktemp -d "${build_dir}/payload.XXXXXX")"
trap 'rm -rf -- "${staging}"' EXIT
source_digest="$(python3 "${packager}" source-digest "${repository_dir}")"

# Separate artifacts keep this cross-RID publish away from active desktop builds
# and permit the release pipeline to use a sealed read-only source checkout.
"${dotnet}" publish "${project}" --configuration Release --runtime "linux-${architecture}" \
    --self-contained true --artifacts-path "${build_dir}/dotnet" --output "${staging}" \
    -p:RestoreLockedMode=true \
    -p:PublishAot=false -p:PublishTrimmed=false -p:UseAppHost=true \
    -p:DebugType=None -p:DebugSymbols=false
mkdir -p "${staging}/legal"
runtime_package="${NUGET_PACKAGES}/microsoft.netcore.app.runtime.linux-${architecture}/10.0.11"
cp "${runtime_package}/LICENSE.TXT" "${staging}/legal/DOTNET-LICENSE.txt"
cp "${runtime_package}/THIRD-PARTY-NOTICES.TXT" "${staging}/legal/DOTNET-THIRD-PARTY-NOTICES.txt"
cp "${repository_dir}/LICENSE" "${staging}/legal/GHOSTSHELL-LICENSE.txt"
cp "${repository_dir}/licenses/SMBLIBRARY-LGPL-3.0.txt" \
    "${repository_dir}/licenses/GPL-3.0.txt" \
    "${repository_dir}/licenses/SMBLIBRARY-SOURCE.json" \
    "${repository_dir}/licenses/SMBLIBRARY-SOURCE-AND-RELINKING.md" \
    "${repository_dir}/licenses/THIRD-PARTY-NOTICES.md" \
    "${staging}/legal/"
cp "${repository_dir}/licenses/SQLCLIENT-MIT.txt" "${staging}/legal/SqlClient-MIT.txt"
"${script_dir}/build-workspace-network-gateway.sh" --rid "linux-${architecture}"
gateway_arch="${architecture}"
[[ "${architecture}" != x64 ]] || gateway_arch=amd64
cp "${repository_dir}/native/artifacts/linux-${architecture}/ghostshell-workspace-gateway-linux-${gateway_arch}" "${staging}/ghostshell-relay-gateway"
cp "${repository_dir}/native/artifacts/linux-${architecture}/workspace-network-gateway-THIRD-PARTY-NOTICES.md" "${staging}/legal/"
cp "${repository_dir}/native/artifacts/linux-${architecture}/workspace-network-gateway-GO-LICENSE.txt" "${staging}/legal/"
backend_version="$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(next(k.split("/",1)[1] for k,v in d["libraries"].items() if k.startswith("GhostShell.Backend/") and v["type"]=="project"))' "${staging}/GhostShell.Backend.deps.json")"
catalog="workspace-backend-managed-components.json"
[[ "${architecture}" != x64 ]] || catalog="workspace-backend-x64-managed-components.json"
"${dotnet}" run --project "${repository_dir}/tools/GhostShell.Packaging/GhostShell.Packaging.csproj" \
    --configuration Release --artifacts-path "${build_dir}/packaging-dotnet" -p:RestoreLockedMode=true -- \
    workspace-backend-evidence "${staging}" "${repository_dir}/licenses" \
    "${repository_dir}/licenses/${catalog}" "${NUGET_PACKAGES}" "${backend_version}"
python3 "${packager}" build "${repository_dir}" "${distribution}" "${staging}" "${source_digest}" "${expected_sdk}"
python3 "${packager}" verify "${repository_dir}" "${distribution}"
echo "Built on-demand Linux ${architecture} backend at ${distribution}."
