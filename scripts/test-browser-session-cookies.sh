#!/usr/bin/env bash
set -euo pipefail

# Uses only a disposable profile and fixture cookies. Never opens user data.
repository_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="${1:-${repository_dir}/native/artifacts/osx-arm64/cef}"
[[ "$(uname -s):$(uname -m)" == Darwin:arm64 ]] || { echo "Requires Apple Silicon macOS." >&2; exit 1; }
[[ -f "${runtime}/cef-runtime-build-receipt.json" ]] || { echo "Build the CEF runtime first." >&2; exit 1; }
test_dir="$(mktemp -d "${TMPDIR:-/tmp}/ghostshell-cookie-restart.XXXXXX")"
trap 'rm -rf -- "${test_dir}"' EXIT
app="${test_dir}/SessionCookies.app"
mkdir -p "${app}/Contents/MacOS" "${app}/Contents/Frameworks" "${test_dir}/state"
cp "${repository_dir}/native/browser-profile-tests/Info.plist" "${app}/Contents/Info.plist"
ditto "${runtime}/Chromium Embedded Framework.framework" "${app}/Contents/Frameworks/Chromium Embedded Framework.framework"
for helper_app in "${runtime}/"*.app; do
    ditto "${helper_app}" "${app}/Contents/Frameworks/$(basename "${helper_app}")"
done
cp "${runtime}/libexclr8cef.dylib" "${app}/Contents/Frameworks/"
xcrun clang -std=c11 -Wall -Wextra -Werror \
    -I "${repository_dir}/vendor/exclr8cef/native/shim" \
    "${repository_dir}/native/browser-profile-tests/session_cookies.c" \
    -L "${app}/Contents/Frameworks" -lexclr8cef \
    -Wl,-rpath,@executable_path/../Frameworks \
    -o "${app}/Contents/MacOS/SessionCookies"
codesign --force --sign - "${app}"
helper="${app}/Contents/Frameworks/GhostSHELL Helper.app/Contents/MacOS/GhostSHELL Helper"
"${app}/Contents/MacOS/SessionCookies" write "${test_dir}/state" "${helper}"
# Profile restoration gives CEF a new working-directory identity each run.
mv "${test_dir}/state/profile" "${test_dir}/state/restored-profile"
# A fresh process must restore the durable cookie but neither another profile's
# cookie nor the private context's cookie. No expiry date was set on either.
"${app}/Contents/MacOS/SessionCookies" read "${test_dir}/state" "${helper}"
