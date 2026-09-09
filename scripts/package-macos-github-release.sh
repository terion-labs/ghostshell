#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
dotnet="${GHOSTSHELL_DOTNET:-${repository_dir}/.dotnet/dotnet}"
package_macos="${repository_dir}/scripts/package-macos.sh"
record_signing_evidence="${repository_dir}/scripts/record-macos-signing-evidence.sh"
entitlements="${repository_dir}/tools/GhostShell.Packaging/MacOS/Chromium.entitlements"
version=""
build_version=""
output_dir=""
cef_runtime_root=""
sign_identity=""
notary_profile=""
keychain=""
release_evidence_dir=""
source_seal=""
security_campaign_tool=""
build_artifacts_root=""
channel="osx-arm64-stable"
package_id="app.ghostshell"

usage() {
    cat >&2 <<'EOF'
Usage:
  ./scripts/package-macos-github-release.sh \
    --version <major.minor.patch> \
    --build-version <number[.number...]> \
    --output-dir <new-release-directory> \
    [--cef-runtime-root <verified-runtime-directory>] \
    [--sign-identity <Developer ID Application identity> \
     --notary-profile <notarytool keychain profile>] \
    [--keychain <signing-keychain>] \
    [--release-evidence-dir <outside-release-directory> \
     --source-seal <sealed-source-evidence> \
     --security-campaign-tool <outside-source-tool-dll> \
     --build-artifacts-root <outside-source-directory>]

Builds the pre-signed GhostShell.app, lets pinned Velopack add its updater and
metadata before the final outer signature and notarization, and emits the
portable ZIP, full update package, channel feed, and checksums. With no signing
arguments it creates an ad-hoc signed local release for end-to-end validation.
The output directory must not exist and is published atomically.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --version)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            version="$2"
            shift 2
            ;;
        --build-version)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            build_version="$2"
            shift 2
            ;;
        --output-dir)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            output_dir="$2"
            shift 2
            ;;
        --cef-runtime-root)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            cef_runtime_root="$2"
            shift 2
            ;;
        --sign-identity)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            sign_identity="$2"
            shift 2
            ;;
        --notary-profile)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            notary_profile="$2"
            shift 2
            ;;
        --keychain)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            keychain="$2"
            shift 2
            ;;
        --release-evidence-dir)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            release_evidence_dir="$2"
            shift 2
            ;;
        --source-seal)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            source_seal="$2"
            shift 2
            ;;
        --security-campaign-tool)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            security_campaign_tool="$2"
            shift 2
            ;;
        --build-artifacts-root)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            build_artifacts_root="$2"
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

if [[ -z "${version}" || -z "${build_version}" || -z "${output_dir}" ]]; then
    usage
    exit 64
fi
if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
    echo "The direct macOS release requires an Apple Silicon macOS host." >&2
    exit 1
fi
if [[ ! "${version}" =~ ^[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}$ \
    || ! "${build_version}" =~ ^[0-9]+(\.[0-9]+)*$ ]]; then
    echo "The release and build versions are invalid." >&2
    exit 64
fi
if [[ -n "${sign_identity}" && -z "${notary_profile}" \
    || -z "${sign_identity}" && -n "${notary_profile}" ]]; then
    echo "Developer ID release assembly requires both signing identity and notary profile." >&2
    exit 64
fi
if [[ -n "${notary_profile}" ]]; then
    python3 "${script_dir}/package-workspace-backend.py" verify-release-clearance "${repository_dir}"
    GHOSTSHELL_BACKEND_ARCH=x64 python3 "${script_dir}/package-workspace-backend.py" verify-release-clearance "${repository_dir}"
fi
if [[ -n "${notary_profile}" \
    && ( -z "${release_evidence_dir}" \
        || -z "${source_seal}" \
        || -z "${security_campaign_tool}" \
        || -z "${build_artifacts_root}" ) ]]; then
    echo "Notarized release assembly requires closed source and release evidence inputs." >&2
    exit 64
fi
if [[ -z "${notary_profile}" \
    && ( -n "${release_evidence_dir}" \
        || -n "${source_seal}" \
        || -n "${security_campaign_tool}" \
        || -n "${build_artifacts_root}" \
        || -n "${keychain}" ) ]]; then
    echo "Release evidence and keychain options are valid only for a notarized release." >&2
    exit 64
fi
for required in "${dotnet}" "${package_macos}" "${record_signing_evidence}"; do
    if [[ ! -x "${required}" ]]; then
        echo "Required release executable is unavailable: ${required}" >&2
        exit 1
    fi
done
if [[ ! -f "${entitlements}" || -L "${entitlements}" ]]; then
    echo "The reviewed macOS signing entitlements are unavailable." >&2
    exit 1
fi

output_parent="$(dirname "${output_dir}")"
if [[ ! -d "${output_parent}" || -L "${output_parent}" || -e "${output_dir}" ]]; then
    echo "The release output requires an existing regular parent and a new directory." >&2
    exit 1
fi
output_parent="$(cd -- "${output_parent}" && pwd -P)"
output_dir="${output_parent}/$(basename "${output_dir}")"
working_directory="$(mktemp -d "${output_parent}/.ghostshell-github-release.XXXXXX")"
private_app_parent="${working_directory}/pre-velopack"
velopack_release="${working_directory}/release"
verification_directory="${working_directory}/verification"
vpk_log="${working_directory}/vpk.log"
mkdir -p "${private_app_parent}" "${velopack_release}" "${verification_directory}"
cleanup() {
    rm -rf -- "${working_directory}"
}
trap cleanup EXIT

# Release runners start without repository tools in the per-user tool cache.
# Restore the manifest-pinned Velopack before the expensive application build
# so a fresh runner and a developer machine exercise the same dependency path.
(
    cd "${repository_dir}"
    "${dotnet}" tool restore
)

package_arguments=(
    --version "${version}"
    --build-version "${build_version}"
    --runtime-identifier osx-arm64
    --output "${private_app_parent}/GhostShell.app"
)
dotnet_artifacts_arguments=()
if [[ -n "${cef_runtime_root}" ]]; then
    package_arguments+=(--cef-runtime-root "${cef_runtime_root}")
fi
if [[ -n "${sign_identity}" ]]; then
    # Nested CEF and Native AOT code is signed here. Velopack later signs only
    # UpdateMac and the final outer bundle after adding sq.version.
    package_arguments+=(--sign-identity "${sign_identity}")
    package_arguments+=(--release-evidence-dir "${release_evidence_dir}")
    package_arguments+=(--source-seal "${source_seal}")
    package_arguments+=(--security-campaign-tool "${security_campaign_tool}")
    package_arguments+=(--build-artifacts-root "${build_artifacts_root}")
    dotnet_artifacts_arguments=(--artifacts-path "${build_artifacts_root}")
fi
"${package_macos}" "${package_arguments[@]}"

vpk_identity="${sign_identity:--}"
vpk_arguments=(
    --legacyConsole true
    --skip-updates true
    pack
    --outputDir "${velopack_release}"
    --channel "${channel}"
    --runtime osx-arm64
    --packId "${package_id}"
    --packVersion "${version}"
    --packDir "${private_app_parent}/GhostShell.app"
    --packAuthors "GhostSHELL contributors"
    --packTitle GhostShell
    --mainExe GhostShell
    --delta None
    --noInst true
    --signAppIdentity "${vpk_identity}"
    --signEntitlements "${entitlements}"
    --signDisableDeep true
)
if [[ -n "${notary_profile}" ]]; then
    vpk_arguments+=(--notaryProfile "${notary_profile}")
fi
if [[ -n "${keychain}" ]]; then
    vpk_arguments+=(--keychain "${keychain}")
fi
"${dotnet}" tool run vpk -- "${vpk_arguments[@]}" 2>&1 | /usr/bin/tee "${vpk_log}"

portable_name="${package_id}-${channel}-Portable.zip"
package_name="${package_id}-${version}-${channel}-full.nupkg"
feed_name="releases.${channel}.json"
archive_name="GhostShell-macOS-arm64.zip"
for expected in "${portable_name}" "${package_name}" "${feed_name}"; do
    if [[ ! -f "${velopack_release}/${expected}" || -L "${velopack_release}/${expected}" ]]; then
        echo "Velopack did not produce ${expected}." >&2
        exit 1
    fi
done

/usr/bin/ditto -x -k \
    "${velopack_release}/${portable_name}" \
    "${verification_directory}"
verified_app="${verification_directory}/GhostShell.app"
/usr/bin/codesign --verify --deep --strict --verbose=2 "${verified_app}"
if [[ -n "${notary_profile}" ]]; then
    /usr/bin/xcrun stapler validate "${verified_app}"
    /usr/sbin/spctl --assess --type execute --verbose=2 "${verified_app}"
fi

"${dotnet}" run \
    --project "${repository_dir}/tools/GhostShell.Packaging/GhostShell.Packaging.csproj" \
    --configuration Release \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    --no-restore \
    -- \
    velopack-macos-validate \
    --release-directory "${velopack_release}" \
    --full-package "${velopack_release}/${package_name}" \
    --app "${verified_app}" \
    --version "${version}" \
    --channel "${channel}"
"${dotnet}" run \
    --project "${repository_dir}/tools/GhostShell.AccessibilityAcceptance/GhostShell.AccessibilityAcceptance.csproj" \
    --configuration Release \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    --no-restore \
    -- \
    inspect-package \
    --platform MacOS \
    --build-label "macos-${version}-${build_version}" \
    --package "${verified_app}"

if [[ -n "${notary_profile}" ]]; then
    uuid_pattern='[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89aAbB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}'
    notarization_ids="$(/usr/bin/grep -Eo "${uuid_pattern}" "${vpk_log}" \
        | LC_ALL=C /usr/bin/sort -u || true)"
    notarization_id_count="$(printf '%s\n' "${notarization_ids}" \
        | /usr/bin/awk 'NF { count++ } END { print count + 0 }')"
    accepted_count="$(/usr/bin/grep -Ec '"status"[[:space:]]*:[[:space:]]*"Accepted"' \
        "${vpk_log}" || true)"
    if [[ "${notarization_id_count}" != "1" || "${accepted_count}" != "1" ]]; then
        echo "Velopack did not report exactly one accepted app notarization." >&2
        exit 1
    fi

    notary_result="${working_directory}/notary-result.json"
    /usr/bin/plutil -create xml1 "${notary_result}"
    /usr/bin/plutil -insert id -string "${notarization_ids}" "${notary_result}"
    /usr/bin/plutil -insert status -string Accepted "${notary_result}"
    /usr/bin/plutil -convert json "${notary_result}"
    "${record_signing_evidence}" \
        --app "${verified_app}" \
        --notary-result "${notary_result}" \
        --evidence "${release_evidence_dir}/notarization.json"
fi

/bin/mv "${velopack_release}/${portable_name}" \
    "${velopack_release}/${archive_name}"
python3 "${script_dir}/package-networking-downloads.py" "${repository_dir}" "${verified_app}" "${velopack_release}"
rm -f -- \
    "${velopack_release}/assets.${channel}.json" \
    "${velopack_release}/RELEASES-${channel}"
(
    cd "${velopack_release}"
    /usr/bin/shasum -a 256 "${archive_name}" > "${archive_name}.sha256"
    /usr/bin/shasum -a 256 "${package_name}" > "${package_name}.sha256"
    /usr/bin/shasum -a 256 "${feed_name}" > "${feed_name}.sha256"
)
/bin/mv "${velopack_release}" "${output_dir}"
echo "Created verified direct macOS release assets at ${output_dir}."
