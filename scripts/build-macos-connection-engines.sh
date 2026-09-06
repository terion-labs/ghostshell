#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
artifact_directory="${repository_dir}/native/artifacts/osx-arm64/connection-engines"
expected_go_version="go1.26.3"
tailscale_module="tailscale.com"
tailscale_version="v1.98.2"
tailscale_sum="h1:HP5gt0qyLKtJoDV7PMUvPpiXjMFk4nzXMbm7JdjttMY="
openconnect_version="9.21"
openconnect_sha256="5b32369467db6e5f317aa1ed12cfcbb81ed00bdbc765450b6bfcbdc300944a58"
openssl_version="3.6.4"
openssl_sha256="9bffaa1ad1e07b354c21bd3324ec02fa15579f45a7d0494b3e74bc449b7333ef"

usage() {
    cat >&2 <<'EOF'
Usage: ./scripts/build-macos-connection-engines.sh

Builds the reviewed self-contained macOS arm64 connection engines and writes a
deterministic checksum manifest plus the complete linked-module license notice.
EOF
}

if [[ $# -gt 0 ]]; then
    if [[ $# -eq 1 && ("$1" == "--help" || "$1" == "-h") ]]; then
        usage
        exit 0
    fi

    usage
    exit 64
fi

if [[ "$(uname -s):$(uname -m)" != "Darwin:arm64" ]]; then
    echo "Connection engine assembly requires an Apple Silicon macOS host." >&2
    exit 1
fi
for required_command in curl file go install_name_tool make otool python3 shasum tar xcrun; do
    if ! command -v "${required_command}" >/dev/null 2>&1; then
        echo "Connection engine assembly requires ${required_command}." >&2
        exit 1
    fi
done

export GOTOOLCHAIN="${expected_go_version}+auto"
if [[ "$(go env GOVERSION)" != "${expected_go_version}" ]]; then
    echo "Connection engine assembly requires ${expected_go_version}." >&2
    exit 1
fi
export CGO_ENABLED=0
export GOARCH=arm64
export GOOS=darwin
export LC_ALL=C
export SOURCE_DATE_EPOCH=0
export TZ=UTC
export MACOSX_DEPLOYMENT_TARGET=13.0

staging_directory="$(mktemp -d "${TMPDIR:-/tmp}/ghostshell-connection-engines.XXXXXX")"
cleanup() {
    rm -rf -- "${staging_directory}"
}
trap cleanup EXIT

download_archive() {
    local url="$1"
    local expected_sha256="$2"
    local output="$3"
    curl --fail --location --silent --show-error "${url}" --output "${output}"
    local actual_sha256
    actual_sha256="$(shasum -a 256 "${output}" | awk '{print $1}')"
    if [[ "${actual_sha256}" != "${expected_sha256}" ]]; then
        echo "The downloaded source archive checksum is not reviewed: ${url}" >&2
        exit 1
    fi
}

download_module() {
    local module="$1"
    local version="$2"
    local expected_sum="$3"
    local metadata
    metadata="$(go mod download -json "${module}@${version}")"
    local actual_sum
    actual_sum="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["Sum"])' <<< "${metadata}")"
    if [[ "${actual_sum}" != "${expected_sum}" ]]; then
        echo "The downloaded ${module} ${version} source checksum is not reviewed." >&2
        exit 1
    fi

    python3 -c 'import json,sys; print(json.load(sys.stdin)["Dir"])' <<< "${metadata}"
}

tailscale_directory="$(download_module \
    "${tailscale_module}" \
    "${tailscale_version}" \
    "${tailscale_sum}")"

build_go_executable() {
    local source_directory="$1"
    local package="$2"
    local output_name="$3"
    local linker_flags="${4:--buildid=}"
    (
        cd "${source_directory}"
        go build \
            -mod=readonly \
            -trimpath \
            -buildvcs=false \
            "-ldflags=${linker_flags}" \
            -o "${staging_directory}/${output_name}" \
            "${package}"
    )
    chmod 755 "${staging_directory}/${output_name}"
    if [[ "$(file -b "${staging_directory}/${output_name}")" != *"Mach-O 64-bit executable arm64"* ]]; then
        echo "The ${output_name} payload is not a macOS arm64 executable." >&2
        exit 1
    fi
}

tailscale_linker_flags="-buildid= -X tailscale.com/version.longStamp=${tailscale_version#v} -X tailscale.com/version.shortStamp=${tailscale_version#v}"
build_go_executable "${tailscale_directory}" ./cmd/tailscale tailscale "${tailscale_linker_flags}"
build_go_executable "${tailscale_directory}" ./cmd/tailscaled tailscaled "${tailscale_linker_flags}"

source_directory="${staging_directory}/sources"
mkdir -p "${source_directory}"
openconnect_archive="${source_directory}/openconnect-${openconnect_version}.tar.gz"
openssl_archive="${staging_directory}/openssl-${openssl_version}.tar.gz"
download_archive \
    "https://www.infradead.org/openconnect/download/openconnect-${openconnect_version}.tar.gz" \
    "${openconnect_sha256}" \
    "${openconnect_archive}"
download_archive \
    "https://github.com/openssl/openssl/releases/download/openssl-${openssl_version}/openssl-${openssl_version}.tar.gz" \
    "${openssl_sha256}" \
    "${openssl_archive}"

tar -xzf "${openssl_archive}" -C "${staging_directory}"
tar -xzf "${openconnect_archive}" -C "${staging_directory}"
openssl_directory="${staging_directory}/openssl-${openssl_version}"
openconnect_directory="${staging_directory}/openconnect-${openconnect_version}"

parallelism="$(sysctl -n hw.logicalcpu 2>/dev/null || printf '%s' 4)"
(
    cd "${openssl_directory}"
    CFLAGS="-O2 -ffile-prefix-map=.=/_/" \
        ./Configure \
            darwin64-arm64-cc \
            no-apps \
            no-docs \
            no-shared \
            no-tests \
            --prefix=/opt/ghostshell/connection-engines/openssl \
            --openssldir=/opt/ghostshell/connection-engines/openssl
    make -j"${parallelism}" build_libs
)

openconnect_build="${staging_directory}/openconnect-build"
mkdir -p "${openconnect_build}"
(
    cd "${openconnect_build}"
    PATH=/usr/bin:/bin:/usr/sbin:/sbin \
    OPENSSL_BUILD_ROOT="${openssl_directory}" \
    PKG_CONFIG="${script_dir}/macos-openconnect-pkg-config.sh" \
    ac_cv_func_strchrnul=no \
    CFLAGS="-O2 -ffile-prefix-map=${staging_directory}=/_/" \
        "${openconnect_directory}/configure" \
            --disable-nls \
            --enable-shared \
            --disable-static \
            --without-gnutls \
            --with-openssl \
            --without-libproxy \
            --without-stoken \
            --without-libpskc \
            --without-libpcsclite \
            --without-gssapi \
            --without-lz4 \
            --with-vpnc-script=/usr/bin/false
    PATH=/usr/bin:/bin:/usr/sbin:/sbin \
    OPENSSL_BUILD_ROOT="${openssl_directory}" \
        make -j"${parallelism}" openconnect
)
cp "${openconnect_build}/.libs/openconnect" "${staging_directory}/openconnect"
cp "${openconnect_build}/.libs/libopenconnect.5.dylib" \
    "${staging_directory}/libopenconnect.5.dylib"
chmod 755 "${staging_directory}/openconnect" "${staging_directory}/libopenconnect.5.dylib"
install_name_tool \
    -change /usr/local/lib/libopenconnect.5.dylib @loader_path/libopenconnect.5.dylib \
    "${staging_directory}/openconnect"
install_name_tool \
    -id @loader_path/libopenconnect.5.dylib \
    "${staging_directory}/libopenconnect.5.dylib"

openvpn_directory="${repository_dir}/native/artifacts/osx-arm64/openvpn-engine"
"${script_dir}/build-openvpn-engine.sh" --verify
for payload in ghostshell-openvpn-engine OPENVPN-MPL-2.0.txt OPENVPN-LICENSE.md \
    ASIO-LICENSE.txt LZ4-LICENSE.txt OPENVPN-VERSIONS.txt; do
    cp "${openvpn_directory}/${payload}" "${staging_directory}/${payload}"
done
cp "${openvpn_directory}/THIRD-PARTY-NOTICES.txt" "${staging_directory}/OPENVPN-THIRD-PARTY-NOTICES.txt"

for executable in openconnect ghostshell-openvpn-engine; do
    if [[ "$(file -b "${staging_directory}/${executable}")" != *"Mach-O 64-bit executable arm64"* ]]; then
        echo "The ${executable} payload is not a macOS arm64 executable." >&2
        exit 1
    fi
done
if [[ "$(file -b "${staging_directory}/libopenconnect.5.dylib")" != *"Mach-O 64-bit dynamically linked shared library arm64"* ]]; then
    echo "The libopenconnect payload is not a macOS arm64 library." >&2
    exit 1
fi
for code_file in openconnect ghostshell-openvpn-engine libopenconnect.5.dylib; do
    unexpected_dependencies="$(otool -L "${staging_directory}/${code_file}" \
        | tail -n +2 \
        | awk '{print $1}' \
        | grep -Ev '^(@loader_path/libopenconnect\.5\.dylib|/usr/lib/|/System/Library/)' \
        || true)"
    if [[ -n "${unexpected_dependencies}" ]]; then
        echo "The ${code_file} payload has an unbundled dependency: ${unexpected_dependencies}" >&2
        exit 1
    fi
done

cp "${openconnect_directory}/COPYING.LGPL" "${staging_directory}/OPENCONNECT-LGPL-2.1.txt"
cp "${openssl_directory}/LICENSE.txt" "${staging_directory}/OPENSSL-LICENSE.txt"

relinking="${staging_directory}/OPENCONNECT-SOURCE-AND-RELINKING.md"
cat > "${relinking}" <<EOF
# OpenConnect source and relinking

GhostSHELL invokes OpenConnect ${openconnect_version} as a separate executable and ships
libopenconnect as a replaceable dynamic library. The complete corresponding OpenConnect
source is distributed in GhostShell-networking-sources.zip beside the app in the
same GitHub release, at connection-engines/sources/openconnect-${openconnect_version}.tar.gz.
The source archive is not needed at runtime and is not embedded in the app. Its SHA-256 is:

    ${openconnect_sha256}

The checked-in scripts/build-macos-connection-engines.sh procedure builds this payload.
It uses OpenSSL ${openssl_version} from the source archive at
https://github.com/openssl/openssl/releases/download/openssl-${openssl_version}/openssl-${openssl_version}.tar.gz
with SHA-256 ${openssl_sha256}. A modified libopenconnect can be substituted beside the
openconnect executable using the install name @loader_path/libopenconnect.5.dylib.
EOF

go_root="$(go env GOROOT)"
go_license_source="${go_root}/LICENSE"
if [[ ! -f "${go_license_source}" ]]; then
    go_license_source="$(dirname "${go_root}")/LICENSE"
fi
if [[ ! -f "${go_license_source}" || -L "${go_license_source}" ]]; then
    echo "The pinned Go toolchain license is unavailable." >&2
    exit 1
fi
cp "${go_license_source}" "${staging_directory}/GO-LICENSE.txt"

linked_modules="${staging_directory}/linked-modules.tsv"
{
    printf '%s\t%s\n' "${tailscale_module}" "${tailscale_version}"
    for executable in tailscale tailscaled; do
        go version -m "${staging_directory}/${executable}" \
            | awk -F '\t' '$2 == "dep" { print $3 "\t" $4 }'
    done
} | LC_ALL=C sort -u > "${linked_modules}"

notices="${staging_directory}/THIRD-PARTY-NOTICES.md"
{
    printf '# Bundled connection engine notices\n\n'
    printf 'GhostSHELL bundles Tailscale %s, OpenConnect %s, and ghostshell-openvpn-engine as separate executables. OpenVPN component versions and licenses are recorded in OPENVPN-VERSIONS.txt and OPENVPN-THIRD-PARTY-NOTICES.txt. WireGuard is linked into the workspace gateway. The Go programs were built with %s; its license is shipped as GO-LICENSE.txt.\n' \
        "${tailscale_version#v}" "${openconnect_version}" "${expected_go_version}"
    while IFS=$'\t' read -r module version; do
        if [[ "${module}:${version}" == "${tailscale_module}:${tailscale_version}" ]]; then
            module_directory="${tailscale_directory}"
        else
            module_directory="$(go mod download -json "${module}@${version}" \
                | python3 -c 'import json,sys; print(json.load(sys.stdin)["Dir"])')"
        fi
        license_source="$(find "${module_directory}" -maxdepth 1 -type f \
            \( -iname 'LICENSE*' -o -iname 'COPYING*' -o -iname 'NOTICE*' \) \
            -print | LC_ALL=C sort | sed -n '1p')"
        if [[ -z "${license_source}" || ! -f "${license_source}" ]]; then
            echo "No license file was found for ${module} ${version}." >&2
            exit 1
        fi

        printf '\n## %s %s\n\n' "${module}" "${version}"
        printf '```text\n'
        sed -e 's/```/` ` `/g' "${license_source}"
        printf '\n```\n'
    done < "${linked_modules}"

    for component in \
        "OpenConnect ${openconnect_version}:${openconnect_directory}/COPYING.LGPL" \
        "OpenSSL ${openssl_version}:${openssl_directory}/LICENSE.txt"; do
        name="${component%%:*}"
        license_source="${component#*:}"
        printf '\n## %s\n\n```text\n' "${name}"
        sed -e 's/```/` ` `/g' "${license_source}"
        printf '\n```\n'
    done
} > "${notices}"
rm "${linked_modules}"

manifest="${staging_directory}/MANIFEST.sha256"
(
    cd "${staging_directory}"
    shasum -a 256 \
        ghostshell-openvpn-engine \
        tailscale \
        tailscaled \
        openconnect \
        libopenconnect.5.dylib \
        GO-LICENSE.txt \
        OPENCONNECT-LGPL-2.1.txt \
        OPENCONNECT-SOURCE-AND-RELINKING.md \
        OPENSSL-LICENSE.txt \
        OPENVPN-MPL-2.0.txt \
        OPENVPN-LICENSE.md \
        ASIO-LICENSE.txt \
        LZ4-LICENSE.txt \
        OPENVPN-VERSIONS.txt \
        OPENVPN-THIRD-PARTY-NOTICES.txt \
        THIRD-PARTY-NOTICES.md \
        sources/openconnect-${openconnect_version}.tar.gz \
        > "${manifest}"
    shasum -a 256 -c "${manifest}"
)

rm -rf -- "${artifact_directory}"
mkdir -p "${artifact_directory}"
for payload in \
    ghostshell-openvpn-engine tailscale tailscaled openconnect libopenconnect.5.dylib \
    GO-LICENSE.txt OPENCONNECT-LGPL-2.1.txt OPENCONNECT-SOURCE-AND-RELINKING.md \
    OPENSSL-LICENSE.txt OPENVPN-MPL-2.0.txt OPENVPN-LICENSE.md ASIO-LICENSE.txt \
    LZ4-LICENSE.txt OPENVPN-VERSIONS.txt OPENVPN-THIRD-PARTY-NOTICES.txt \
    THIRD-PARTY-NOTICES.md MANIFEST.sha256 sources; do
    mv "${staging_directory}/${payload}" "${artifact_directory}/${payload}"
done

echo "Built reviewed macOS arm64 connection engines in ${artifact_directory}."
