#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
mode="${1:---full}"
case "${mode}" in
    --quick|--full) ;;
    *) echo "Usage: $0 [--quick|--full]" >&2; exit 64 ;;
esac

command -v go >/dev/null || { echo "The pinned Go toolchain is required for native networking checks." >&2; exit 1; }
export GOTOOLCHAIN=go1.26.3+auto
[[ "$(go env GOVERSION)" == go1.26.3 ]] || { echo "Native networking checks require Go 1.26.3." >&2; exit 1; }
go_root="$(go env GOROOT)"
module_dir="${repository_dir}/native/workspace-network-gateway"
unformatted="$(find "${module_dir}" -name '*.go' -type f -exec "${go_root}/bin/gofmt" -l {} +)"
if [[ -n "${unformatted}" ]]; then
    echo "Native Go formatting differs:" >&2
    printf '%s\n' "${unformatted}" >&2
    exit 1
fi

if [[ "${mode}" == --full ]]; then
    (
        cd "${module_dir}"
        go mod verify
        go test -mod=readonly -count=1 ./...
        CGO_ENABLED=1 go test -mod=readonly -race -count=1 ./...
        go vet -mod=readonly ./...
    )
    if [[ "$(uname -s):$(uname -m)" == Darwin:arm64 ]]; then
        if [[ ! -f "${repository_dir}/native/artifacts/openvpn-engine-build/build/CMakeCache.txt" ]]; then
            "${script_dir}/build-openvpn-engine.sh"
        fi
        "${script_dir}/build-openvpn-engine.sh" --test
        if [[ "$(sw_vers -productVersion | cut -d. -f1)" -ge 15 ]]; then
            (
                test_directory="$(mktemp -d "${TMPDIR:-/tmp}/asura-native-gate.XXXXXX")"
                trap 'rm -rf -- "${test_directory}"' EXIT
                cd "${module_dir}"
                CGO_ENABLED=0 go build -mod=readonly -o "${test_directory}/workspace-network-gateway" ./cmd/workspace-network-gateway
                ASURA_RUNTIME_TEST_GATEWAY="${test_directory}/workspace-network-gateway" \
                    "${script_dir}/build-workspace-runtime.sh" --test
            )
        fi
    fi
fi
