#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
sdk_dir="${repository_dir}/.dotnet"
sdk_version="10.0.303"
installer_sha256="082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e"

if [[ -x "${sdk_dir}/dotnet" ]] &&
   [[ "$("${sdk_dir}/dotnet" --version)" == "${sdk_version}" ]]; then
    echo ".NET SDK ${sdk_version} is already installed in ${sdk_dir}."
else
    installer="$(mktemp -t ghostshell-dotnet-install.XXXXXX)"
    trap 'rm -f "${installer}"' EXIT

    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "${installer}"
    actual_installer_sha256="$(shasum -a 256 "${installer}" | awk '{print $1}')"
    if [[ "${actual_installer_sha256}" != "${installer_sha256}" ]]; then
        echo "The dotnet-install.sh checksum changed; review it before updating the pin." >&2
        exit 1
    fi
    bash "${installer}" --version "${sdk_version}" --install-dir "${sdk_dir}" --no-path
    echo "Installed .NET SDK ${sdk_version} in ${sdk_dir}."
fi

"${sdk_dir}/dotnet" tool restore
"${script_dir}/install-hooks.sh"

if [[ "${GHOSTSHELL_SKIP_NATIVE-0}" != "1" ]]; then
    host_os="$(uname -s)"
    host_arch="$(uname -m)"
    case "${host_os}:${host_arch}" in
        Darwin:arm64)
            native_rid="osx-arm64"
            build_workspace_network_gateway=true
            ;;
        Darwin:x86_64)
            native_rid="osx-x64"
            ;;
        Linux:aarch64|Linux:arm64)
            native_rid="linux-arm64"
            ;;
        Linux:x86_64)
            native_rid="linux-x64"
            ;;
        *)
            echo "GhostSHELL has no native terminal build for ${host_os} ${host_arch}." >&2
            exit 1
            ;;
    esac

    "${script_dir}/build-libghostty-vt.sh" --rid "${native_rid}"
    if [[ "${build_workspace_network_gateway:-false}" == true ]]; then
        "${script_dir}/build-workspace-network-gateway.sh" --rid osx-arm64
        "${script_dir}/build-workspace-runtime.sh"
        "${script_dir}/build-openvpn-engine.sh"
        "${script_dir}/build-macos-connection-engines.sh"
    fi
fi
