#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
module_dir="${repository_dir}/native/workspace-network-gateway"
expected_go_version="go1.26.3"
rids=()

usage() {
    cat >&2 <<'EOF'
Usage: ./scripts/build-workspace-network-gateway.sh --rid <osx-arm64|linux-arm64>
       ./scripts/build-workspace-network-gateway.sh --all

Builds the host and guest workspace packet gateway executables with the pinned
Go toolchain and writes their deterministic SHA-256 manifests and legal notices
under native/artifacts/<rid>.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --rid)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            rids+=("$2")
            shift 2
            ;;
        --all)
            rids=(osx-arm64 linux-arm64 osx-x64 linux-x64)
            shift
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

if [[ ${#rids[@]} -eq 0 ]]; then
    usage
    exit 64
fi
if ! command -v go >/dev/null 2>&1; then
    echo "Go ${expected_go_version#go} is required to build the workspace network gateway." >&2
    exit 1
fi
export GOTOOLCHAIN="${expected_go_version}+auto"
actual_go_version="$(go env GOVERSION)"
if [[ "${actual_go_version}" != "${expected_go_version}" ]]; then
    echo "Workspace network gateway requires ${expected_go_version}; found ${actual_go_version}." >&2
    exit 1
fi
if [[ ! -f "${module_dir}/go.mod" \
    || ! -d "${module_dir}/cmd/workspace-network-gateway" ]]; then
    echo "The workspace network gateway Go module is incomplete." >&2
    exit 1
fi

export CGO_ENABLED=0
export LC_ALL=C
export SOURCE_DATE_EPOCH=0
export TZ=UTC

(
    cd "${module_dir}"
    go test ./...
)

for rid in "${rids[@]}"; do
    goarch=arm64
    case "${rid}" in
        osx-arm64)
            goos=darwin
            artifact="asura-workspace-gateway-darwin-arm64"
            expected_file_description="Mach-O 64-bit executable arm64"
            ;;
        linux-arm64)
            goos=linux
            artifact="asura-workspace-gateway-linux-arm64"
            expected_file_description="ELF 64-bit LSB executable, ARM aarch64"
            ;;
        osx-x64)
            goos=darwin
            goarch=amd64
            artifact="asura-workspace-gateway-darwin-amd64"
            expected_file_description="Mach-O 64-bit executable x86_64"
            ;;
        linux-x64)
            goos=linux
            goarch=amd64
            artifact="asura-workspace-gateway-linux-amd64"
            expected_file_description="ELF 64-bit LSB executable, x86-64"
            ;;
        *)
            echo "Unsupported workspace network gateway RID: ${rid}" >&2
            exit 64
            ;;
    esac

    artifact_directory="${repository_dir}/native/artifacts/${rid}"
    staging_directory="$(mktemp -d "${TMPDIR:-/tmp}/asura-network-gateway.XXXXXX")"
    cleanup() {
        rm -rf -- "${staging_directory}"
    }
    trap cleanup EXIT

    staged_artifact="${staging_directory}/${artifact}"
    (
        cd "${module_dir}"
        GOOS="${goos}" GOARCH="${goarch}" go build \
            -trimpath \
            -buildvcs=false \
            -ldflags=-buildid= \
            -o "${staged_artifact}" \
            ./cmd/workspace-network-gateway
    )
    chmod 755 "${staged_artifact}"
    if [[ "$(file -b "${staged_artifact}")" != *"${expected_file_description}"* ]]; then
        echo "The ${rid} workspace network gateway has the wrong executable format." >&2
        exit 1
    fi

    go_license="${staging_directory}/workspace-network-gateway-GO-LICENSE.txt"
    go_root="$(go env GOROOT)"
    go_license_source="${go_root}/LICENSE"
    if [[ ! -f "${go_license_source}" ]]; then
        go_license_source="$(dirname "${go_root}")/LICENSE"
    fi
    if [[ ! -f "${go_license_source}" || -L "${go_license_source}" ]]; then
        echo "The pinned Go toolchain license is unavailable." >&2
        exit 1
    fi
    cp "${go_license_source}" "${go_license}"

    packaged_guest_artifact=""
    if [[ "${rid}" == linux-arm64 ]]; then
        packaged_guest_artifact="${staging_directory}/workspace-gateway"
        cp "${staged_artifact}" "${packaged_guest_artifact}"
        chmod 755 "${packaged_guest_artifact}"
    fi

    linked_modules="${staging_directory}/linked-modules.tsv"
    go version -m "${staged_artifact}" \
        | awk -F '\t' '$2 == "dep" { print $3 "\t" $4 }' \
        | LC_ALL=C sort \
        > "${linked_modules}"
    if [[ ! -s "${linked_modules}" ]]; then
        echo "The workspace network gateway contains no inspectable module metadata." >&2
        exit 1
    fi

    notices="${staging_directory}/workspace-network-gateway-THIRD-PARTY-NOTICES.md"
    {
        printf '# Workspace network gateway notices\n\n'
        printf 'Built with %s. The Go toolchain and standard library license is shipped as workspace-network-gateway-GO-LICENSE.txt beside this notice.\n\n' "${expected_go_version}"
        printf 'The executable contains the following third-party Go modules. Each module license follows its module entry.\n'
        while IFS=$'\t' read -r module version; do
            module_directory="$({
                cd "${module_dir}"
                go list -m -f '{{.Dir}}' "${module}@${version}"
            })"
            license_source="$(find "${module_directory}" -maxdepth 1 -type f \
                \( -iname 'LICENSE*' -o -iname 'COPYING*' -o -iname 'NOTICE*' \) \
                -print | LC_ALL=C sort | sed -n '1p')"
            if [[ -z "${license_source}" || ! -f "${license_source}" ]]; then
                echo "No license file was found for ${module} ${version}." >&2
                exit 1
            fi

            printf '\n## %s %s\n\n' "${module}" "${version}"
            printf 'Source: https://%s\n\n' "${module}"
            printf '```text\n'
            sed -e 's/```/` ` `/g' "${license_source}"
            printf '\n```\n'
        done < "${linked_modules}"
    } > "${notices}"
    rm "${linked_modules}"

    manifest="${staging_directory}/workspace-network-gateway-MANIFEST.sha256"
    (
        cd "${staging_directory}"
        shasum -a 256 \
            "${artifact}" \
            ${packaged_guest_artifact:+workspace-gateway} \
            workspace-network-gateway-GO-LICENSE.txt \
            workspace-network-gateway-THIRD-PARTY-NOTICES.md \
            > "${manifest}"
        shasum -a 256 -c "${manifest}"
    )

    mkdir -p "${artifact_directory}"
    mv "${staged_artifact}" "${artifact_directory}/${artifact}"
    if [[ -n "${packaged_guest_artifact}" ]]; then
        mv "${packaged_guest_artifact}" "${artifact_directory}/workspace-gateway"
    fi
    mv "${go_license}" "${artifact_directory}/workspace-network-gateway-GO-LICENSE.txt"
    mv "${notices}" "${artifact_directory}/workspace-network-gateway-THIRD-PARTY-NOTICES.md"
    mv "${manifest}" "${artifact_directory}/workspace-network-gateway-MANIFEST.sha256"
    rmdir "${staging_directory}"
    trap - EXIT

    echo "Built ${artifact_directory}/${artifact}"
done
