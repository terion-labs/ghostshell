#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_directory="${repository_root}/native/artifacts/osx-arm64/openvpn-engine"
# RID directories contain distributable payloads only, never compiler outputs.
build_directory="${repository_root}/native/artifacts/openvpn-engine-build"
core="${build_directory}/openvpn3-18edfae7e7fd8051c93bd4746ec69be91eb02dbb"
openssl="${build_directory}/openssl-3.6.4"
lz4="${build_directory}/lz4-1.10.0"
# CMake caches absolute source, build, and dependency paths. Regenerate its
# configuration for both builds and tests so a relocated checkout stays usable.
cmake_configuration=(
    -S "${repository_root}/native/openvpn-engine" -B "${build_directory}/build"
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0
    -DOPENVPN_SOURCE="${core}" -DASIO_SOURCE="${build_directory}/asio-asio-1-32-0"
    -DOPENSSL_SOURCE="${openssl}" -DLZ4_SOURCE="${lz4}"
)
if [[ "${1:-}" == --help ]]; then
    echo "Usage: scripts/build-openvpn-engine.sh [--verify|--test]"
    exit 0
fi
if [[ "${1:-}" == --test ]]; then
    if [[ ! -f "${build_directory}/build/CMakeCache.txt" ]]; then
        echo "Build the pinned engine dependencies with scripts/build-openvpn-engine.sh first." >&2
        exit 1
    fi
    rm -rf -- "${build_directory}/build/CMakeCache.txt" "${build_directory}/build/CMakeFiles"
    cmake "${cmake_configuration[@]}"
    cmake --build "${build_directory}/build" --parallel 4
    ctest --test-dir "${build_directory}/build" --output-on-failure
    exit 0
fi
if [[ "${1:-}" == --verify ]]; then
    cd "${output_directory}"
    shasum -a 256 --check SHA256SUMS
    exit 0
fi
if [[ $# -ne 0 || "$(uname -s):$(uname -m)" != Darwin:arm64 ]]; then
    echo "Build the OpenVPN engine on Apple Silicon macOS." >&2
    exit 64
fi
for executable in curl shasum tar cmake make xcrun otool; do
    command -v "${executable}" >/dev/null || { echo "Missing build tool: ${executable}" >&2; exit 1; }
done
# Retain these pinned source/dependency builds so the mandatory gate can rebuild
# current first-party C++ and run its tests without downloading dependencies.
staging_directory="${build_directory}"
mkdir -p "${staging_directory}"
export MACOSX_DEPLOYMENT_TARGET=13.0 LC_ALL=C SOURCE_DATE_EPOCH=0

download() {
    local url="$1" expected="$2" filename="$3"
    curl --fail --location --silent --show-error "${url}" --output "${staging_directory}/${filename}"
    local actual
    actual="$(shasum -a 256 "${staging_directory}/${filename}" | awk '{print $1}')"
    [[ "${actual}" == "${expected}" ]] || { echo "Source checksum mismatch: ${filename}" >&2; exit 1; }
    tar -xzf "${staging_directory}/${filename}" -C "${staging_directory}"
}

# Each archive is pinned independently. Build tools are host prerequisites;
# every non-system library is compiled from these sources and linked statically.
download https://codeload.github.com/OpenVPN/openvpn3/tar.gz/18edfae7e7fd8051c93bd4746ec69be91eb02dbb \
    94df957ac9782ce077b55dc4451fc7aa087ebfaaa26962fbb3fad7596e91d97a core.tar.gz
download https://codeload.github.com/chriskohlhoff/asio/tar.gz/refs/tags/asio-1-32-0 \
    f1b94b80eeb00bb63a3c8cef5047d4e409df4d8a3fe502305976965827d95672 asio.tar.gz
download https://github.com/openssl/openssl/releases/download/openssl-3.6.4/openssl-3.6.4.tar.gz \
    9bffaa1ad1e07b354c21bd3324ec02fa15579f45a7d0494b3e74bc449b7333ef openssl.tar.gz
download https://codeload.github.com/lz4/lz4/tar.gz/refs/tags/v1.10.0 \
    537512904744b35e232912055ccf8ec66d768639ff3abe5788d90d792ec5f48b lz4.tar.gz
(
    cd "${openssl}"
    ./Configure darwin64-arm64-cc no-shared no-tests no-module no-legacy \
        --prefix=/opt/asura/openvpn-engine -mmacosx-version-min=13.0 > "${staging_directory}/openssl.log" 2>&1
    make -j8 build_libs >> "${staging_directory}/openssl.log" 2>&1
) || { tail -60 "${staging_directory}/openssl.log" >&2; exit 1; }
make -C "${lz4}/lib" -j8 liblz4.a CFLAGS='-O2 -mmacosx-version-min=13.0'
rm -rf -- "${build_directory}/build/CMakeCache.txt" "${build_directory}/build/CMakeFiles"
cmake "${cmake_configuration[@]}"
cmake --build "${staging_directory}/build" --parallel 4
ctest --test-dir "${staging_directory}/build" --output-on-failure
binary="${staging_directory}/build/asura-openvpn-engine"
if otool -L "${binary}" | tail -n +2 | awk '{print $1}' | grep -Ev '^(/usr/lib/|/System/Library/)'; then
    echo "OpenVPN engine contains an unbundled runtime dependency." >&2
    exit 1
fi
mkdir -p "${output_directory}"
install -m 755 "${binary}" "${output_directory}/asura-openvpn-engine"
cp "${core}/LICENSES/MPL-2.0.txt" "${output_directory}/OPENVPN-MPL-2.0.txt"
cp "${core}/LICENSE.md" "${output_directory}/OPENVPN-LICENSE.md"
cp "${openssl}/LICENSE.txt" "${output_directory}/OPENSSL-LICENSE.txt"
cp "${staging_directory}/asio-asio-1-32-0/asio/LICENSE_1_0.txt" "${output_directory}/ASIO-LICENSE.txt"
cp "${lz4}/lib/LICENSE" "${output_directory}/LZ4-LICENSE.txt"
cp "${repository_root}/native/openvpn-engine/THIRD-PARTY-NOTICES.txt" "${output_directory}/THIRD-PARTY-NOTICES.txt"
cp "${repository_root}/native/openvpn-engine/VERSIONS.txt" "${output_directory}/OPENVPN-VERSIONS.txt"
(
    cd "${output_directory}"
    shasum -a 256 asura-openvpn-engine OPENVPN-MPL-2.0.txt OPENVPN-LICENSE.md \
        OPENSSL-LICENSE.txt ASIO-LICENSE.txt LZ4-LICENSE.txt THIRD-PARTY-NOTICES.txt OPENVPN-VERSIONS.txt > SHA256SUMS
)
echo "Built and tested ${output_directory}/asura-openvpn-engine"
