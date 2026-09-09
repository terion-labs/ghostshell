#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
entitlements="${repository_dir}/tools/Asura.Packaging/MacOS/Chromium.entitlements"
workspace_entitlements="${repository_dir}/tools/Asura.Packaging/MacOS/WorkspaceRuntime.entitlements"
app=""
identity=""
notary_profile=""
evidence=""
record_signing_evidence="${repository_dir}/scripts/record-macos-signing-evidence.sh"

usage() {
    cat >&2 <<'EOF'
Usage:
  ./scripts/sign-notarize-macos.sh \
    --app <path/to/Asura.app> \
    --identity <Developer ID Application identity> \
    [--notary-profile <notarytool keychain profile>] \
    [--evidence <outside-app/notarization.json>]

Signs the already-assembled managed-runtime native code, CEF framework, five
helper apps, and outer Asura bundle in nested-code order. If a notary
profile is provided, submits a temporary ZIP, staples the ticket, and validates
it. Use identity '-' only for a locally trusted ad-hoc development build;
ad-hoc builds cannot be notarized or distributed through a browser.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --app)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            app="$2"
            shift 2
            ;;
        --identity)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            identity="$2"
            shift 2
            ;;
        --notary-profile)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            notary_profile="$2"
            shift 2
            ;;
        --evidence)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            evidence="$2"
            shift 2
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

if [[ -z "${app}" || -z "${identity}" ]]; then
    usage
    exit 64
fi
if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "macOS signing requires a macOS host." >&2
    exit 1
fi
if [[ ! -d "${app}" || "$(basename "${app}")" != "Asura.app" ]]; then
    echo "--app must name an assembled Asura.app directory." >&2
    exit 1
fi
if [[ ! -f "${entitlements}" ]]; then
    echo "Chromium hardened-runtime entitlements are unavailable." >&2
    exit 1
fi
if [[ "${identity}" == "-" && -n "${notary_profile}" ]]; then
    echo "Ad-hoc signatures cannot be notarized." >&2
    exit 64
fi
if [[ -n "${notary_profile}" && -z "${evidence}" ]]; then
    echo "Notarized distribution requires a closed --evidence record." >&2
    exit 64
fi
if [[ -z "${notary_profile}" && -n "${evidence}" ]]; then
    echo "Signing evidence is valid only for a notarized distribution." >&2
    exit 64
fi
if [[ -n "${evidence}" ]]; then
    evidence="$(cd -- "$(dirname -- "${evidence}")" && pwd -P)/$(basename -- "${evidence}")"
    app_canonical="$(cd -- "$(dirname -- "${app}")" && pwd -P)/$(basename -- "${app}")"
    if [[ "${evidence}" == "${app_canonical}"/* || -e "${evidence}" ]]; then
        echo "Signing evidence must be a new file outside Asura.app." >&2
        exit 64
    fi
fi

frameworks="${app}/Contents/Frameworks"
cef_framework="${frameworks}/Chromium Embedded Framework.framework"
required_nested=(
    "${cef_framework}"
    "${frameworks}/libexclr8cef.dylib"
    "${app}/Contents/MacOS/libexclr8cef.dylib"
    "${app}/Contents/MacOS/libghostty-vt.dylib"
    "${frameworks}/Asura Helper.app"
    "${frameworks}/Asura Helper (Alerts).app"
    "${frameworks}/Asura Helper (GPU).app"
    "${frameworks}/Asura Helper (Plugin).app"
    "${frameworks}/Asura Helper (Renderer).app"
)
for nested in "${required_nested[@]}"; do
    if [[ ! -e "${nested}" ]]; then
        echo "The assembled bundle is missing nested code: $(basename "${nested}")." >&2
        exit 1
    fi
done

sign_plain() {
    local arguments=(--force)
    if [[ "${identity}" != "-" ]]; then
        arguments+=(--options runtime --timestamp)
    fi
    arguments+=(--sign "${identity}" "$1")
    /usr/bin/codesign "${arguments[@]}"
}

sign_chromium_bundle() {
    local arguments=(
        --force
        --entitlements "${entitlements}"
    )
    if [[ "${identity}" != "-" ]]; then
        arguments+=(--options runtime --timestamp)
    fi
    arguments+=(--sign "${identity}" "$1")
    /usr/bin/codesign "${arguments[@]}"
}

sign_workspace_runtime() {
    local arguments=(--force --entitlements "${workspace_entitlements}")
    if [[ "${identity}" != "-" ]]; then
        arguments+=(--options runtime --timestamp)
    fi
    /usr/bin/codesign "${arguments[@]}" --sign "${identity}" "$1"
}

# Sign leaf Mach-O libraries before the framework and app bundles that contain
# them. `--deep` is intentionally avoided: it can silently apply the wrong
# entitlements to nested Chromium helpers.
while IFS= read -r -d '' library; do
    sign_plain "${library}"
done < <(find "${cef_framework}/Libraries" -type f \
    \( -name '*.dylib' -o -name '*.so' \) -print0)
sign_plain "${cef_framework}"

sign_plain "${frameworks}/libexclr8cef.dylib"
while IFS= read -r -d '' runtime_file; do
    if [[ "${runtime_file}" == "${app}/Contents/MacOS/Asura" ]]; then
        continue
    fi

    runtime_description="$(/usr/bin/file -b "${runtime_file}")"
    if [[ "${runtime_description}" == Mach-O* ]]; then
        if [[ "${runtime_file}" == "${app}/Contents/MacOS/runtimes/osx-arm64/workspace-runtime/workspace-runtime" ]]; then
            sign_workspace_runtime "${runtime_file}"
        else
            sign_plain "${runtime_file}"
        fi
    fi
done < <(find "${app}/Contents/MacOS" -type f -print0)

for helper in \
    "${frameworks}/Asura Helper.app" \
    "${frameworks}/Asura Helper (Alerts).app" \
    "${frameworks}/Asura Helper (GPU).app" \
    "${frameworks}/Asura Helper (Plugin).app" \
    "${frameworks}/Asura Helper (Renderer).app"; do
    sign_chromium_bundle "${helper}"
done

sign_chromium_bundle "${app}"
/usr/bin/codesign --verify --deep --strict --verbose=2 "${app}"

if [[ -z "${notary_profile}" ]]; then
    exit 0
fi

notary_directory="$(mktemp -d "${TMPDIR:-/tmp}/asura-notary.XXXXXX")"
notary_zip="${notary_directory}/Asura.zip"
evidence_staging=""
cleanup() {
    rm -rf -- "${notary_directory}"
    if [[ -n "${evidence_staging}" ]]; then
        rm -f -- "${evidence_staging}"
    fi
}
trap cleanup EXIT

/usr/bin/ditto -c -k --keepParent "${app}" "${notary_zip}"
notary_result="${notary_directory}/notary-result.json"
/usr/bin/xcrun notarytool submit \
    "${notary_zip}" \
    --keychain-profile "${notary_profile}" \
    --wait \
    --output-format json > "${notary_result}"
notarization_id="$(/usr/bin/plutil -extract id raw "${notary_result}")"
notarization_status="$(/usr/bin/plutil -extract status raw "${notary_result}")"
if [[ -z "${notarization_id}" || "${notarization_status}" != "Accepted" ]]; then
    echo "Apple notarization did not return an accepted closed result." >&2
    exit 1
fi
/usr/bin/xcrun stapler staple "${app}"
"${record_signing_evidence}" \
    --app "${app}" \
    --notary-result "${notary_result}" \
    --evidence "${evidence}"
