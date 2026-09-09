#!/usr/bin/env bash
set -euo pipefail
repository_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${repository_dir}"
runtime="${1:-${repository_dir}/native/artifacts/osx-arm64/cef}"
abi="${2:-v2}"
test_dir="$(mktemp -d "${TMPDIR:-/tmp}/asura-auth-probe.XXXXXX")"
trap 'rm -rf -- "${test_dir}"' EXIT
app="${test_dir}/SessionCookies.app"
mkdir -p "${app}/Contents/MacOS" "${app}/Contents/Frameworks" "${test_dir}/state"
cp native/browser-profile-tests/Info.plist "${app}/Contents/Info.plist"
ditto "${runtime}/Chromium Embedded Framework.framework" "${app}/Contents/Frameworks/Chromium Embedded Framework.framework"
for helper in "${runtime}/"*.app; do
    ditto "${helper}" "${app}/Contents/Frameworks/$(basename "${helper}")"
done
cp "${runtime}/libexclr8cef.dylib" "${app}/Contents/Frameworks/"
xcrun clang -std=c11 -Wall -Wextra -Werror \
    -I "${repository_dir}/vendor/exclr8cef/native/shim" \
    native/browser-profile-tests/authentication_origin.c \
    -L "${app}/Contents/Frameworks" -lexclr8cef -framework CoreFoundation \
    -Wl,-rpath,@executable_path/../Frameworks -o "${app}/Contents/MacOS/SessionCookies"
codesign --force --sign - "${app}"
"${app}/Contents/MacOS/SessionCookies" "${test_dir}/state" \
    "${app}/Contents/Frameworks/Asura Helper.app/Contents/MacOS/Asura Helper" "${abi}"
