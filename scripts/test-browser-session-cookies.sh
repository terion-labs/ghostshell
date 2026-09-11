#!/usr/bin/env bash
set -euo pipefail

# Uses only a disposable profile and fixture cookies. Never opens user data.
repository_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runtime="${1:-${repository_dir}/native/artifacts/osx-arm64/cef}"
keychain_mode="${2:-native}"
signing_identity="${ASURA_BROWSER_TEST_SIGNING_IDENTITY:--}"
[[ "${keychain_mode}" == mock || "${keychain_mode}" == native || "${keychain_mode}" == transition || "${keychain_mode}" == denied || "${keychain_mode}" == update ]] || { echo "Keychain mode must be native, mock, transition, denied or update." >&2; exit 1; }
write_mode="${keychain_mode}"
read_mode="${keychain_mode}"
read_operation=read
if [[ "${keychain_mode}" == transition ]]; then
    write_mode=mock
    read_mode=native
    read_operation=read-missing
fi
if [[ "${keychain_mode}" == denied ]]; then
    write_mode=native
    read_mode=native
    read_operation=read-missing
fi
if [[ "${keychain_mode}" == update ]]; then
    [[ "${signing_identity}" != - ]] || { echo "The update probe requires a Developer ID signing identity." >&2; exit 1; }
    write_mode=native
    read_mode=native
fi
[[ "$(uname -s):$(uname -m)" == Darwin:arm64 ]] || { echo "Requires Apple Silicon macOS." >&2; exit 1; }
[[ -f "${runtime}/cef-runtime-build-receipt.json" ]] || { echo "Build the CEF runtime first." >&2; exit 1; }
test_dir="$(mktemp -d "${TMPDIR:-/tmp}/asura-cookie-restart.XXXXXX")"
keychain_service="AsuraTest$(openssl rand -hex 6)"
cleanup() {
    local result=$?
    # Only this run's synthetic item. Never read or delete Chromium/Asura keys.
    if [[ "${keychain_mode}" != mock ]]; then
        if ! security delete-generic-password -s "${keychain_service}" -a Chromium >/dev/null 2>&1; then
            echo "Could not remove disposable Keychain item ${keychain_service}." >&2
            result=1
        fi
    fi
    rm -rf -- "${test_dir}"
    return "${result}"
}
trap cleanup EXIT
app="${test_dir}/SessionCookies.app"
mkdir -p "${app}/Contents/MacOS" "${app}/Contents/Frameworks" "${test_dir}/state"
cp "${repository_dir}/native/browser-profile-tests/Info.plist" "${app}/Contents/Info.plist"
ditto "${runtime}/Chromium Embedded Framework.framework" "${app}/Contents/Frameworks/Chromium Embedded Framework.framework"
python3 - "${repository_dir}/scripts/scope-cef-keychain.py" \
    "${app}/Contents/Frameworks/Chromium Embedded Framework.framework/Chromium Embedded Framework" \
    "${keychain_service}" <<'PY'
import importlib.util
from pathlib import Path
import sys
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location("scope_cef", sys.argv[1])
scope = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scope)
scope.SERVICES["fixture"] = sys.argv[3].encode("ascii") + b"\0"
scope.scope_framework(Path(sys.argv[2]), "release", "fixture")
PY
codesign --force --sign - "${app}/Contents/Frameworks/Chromium Embedded Framework.framework"
for helper_app in "${runtime}/"*.app; do
    ditto "${helper_app}" "${app}/Contents/Frameworks/$(basename "${helper_app}")"
done
cp "${runtime}/libexclr8cef.dylib" "${app}/Contents/Frameworks/"
xcrun clang -std=c11 -Wall -Wextra -Werror \
    -I "${repository_dir}/vendor/exclr8cef/native/shim" \
    "${repository_dir}/native/browser-profile-tests/session_cookies.c" \
    -L "${app}/Contents/Frameworks" -lexclr8cef -framework Security \
    -Wl,-rpath,@executable_path/../Frameworks \
    -o "${app}/Contents/MacOS/SessionCookies"
codesign --force --sign "${signing_identity}" "${app}"
helper="${app}/Contents/Frameworks/Asura Helper.app/Contents/MacOS/Asura Helper"
"${app}/Contents/MacOS/SessionCookies" write "${test_dir}/state" "${helper}" "${write_mode}"
cookie_database="${test_dir}/state/profile/Cookies"
[[ -f "${cookie_database}" ]] || { echo "Fixture cookie database missing." >&2; exit 1; }
encrypted_count="$(sqlite3 "${cookie_database}" "SELECT count(*) FROM cookies WHERE name='login' AND value='' AND length(encrypted_value)>0;")"
[[ "${encrypted_count}" == 1 ]] || { echo "Fixture cookie was not stored encrypted." >&2; exit 1; }
if [[ "${keychain_mode}" == denied ]]; then
    # A different signing identity cannot read the first process's fixture key.
    # It must return without a password dialog or a plaintext-cookie fallback.
    codesign --force --sign - --identifier sh.asura.tests.untrusted-cookie-reader "${app}"
fi
if [[ "${keychain_mode}" == update ]]; then
    original_requirement="$(codesign -dr - "${app}" 2>&1 | sed -n '/designated =>/p')"
    original_hash="$(codesign -dv "${app}" 2>&1 | sed -n '/^CDHash=/p')"
    /usr/bin/plutil -replace CFBundleVersion -string 2 "${app}/Contents/Info.plist"
    codesign --force --sign "${signing_identity}" "${app}"
    [[ "$(codesign -dr - "${app}" 2>&1 | sed -n '/designated =>/p')" == "${original_requirement}" ]]
    [[ "$(codesign -dv "${app}" 2>&1 | sed -n '/^CDHash=/p')" != "${original_hash}" ]]
    mkdir "${test_dir}/updated-installation"
    mv "${app}" "${test_dir}/updated-installation/SessionCookies.app"
    app="${test_dir}/updated-installation/SessionCookies.app"
    helper="${app}/Contents/Frameworks/Asura Helper.app/Contents/MacOS/Asura Helper"
    codesign --verify --strict "${app}"
fi
# Profile restoration gives CEF a new working-directory identity each run.
mv "${test_dir}/state/profile" "${test_dir}/state/restored-profile"
# A fresh process must restore the durable cookie but neither another profile's
# cookie nor the private context's cookie. No expiry date was set on either.
"${app}/Contents/MacOS/SessionCookies" "${read_operation}" "${test_dir}/state" "${helper}" "${read_mode}"
if [[ "${keychain_mode}" == denied ]]; then
    plaintext_count="$(sqlite3 "${test_dir}/state/restored-profile/Cookies" "SELECT count(*) FROM cookies WHERE value<>'';")"
    [[ "${plaintext_count}" == 0 ]] || { echo "Denied Keychain access left plaintext cookies." >&2; exit 1; }
fi
