#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
dotnet="${ASURA_DOTNET:-${repository_dir}/.dotnet/dotnet}"
configuration="Release"
version=""
build_version=""
output=""
runtime_identifier=""
cef_runtime_root=""
sign_identity=""
notary_profile=""
release_evidence_dir=""
source_seal=""
security_campaign_tool=""
build_artifacts_root=""
native_aot_linker="${ASURA_NATIVE_AOT_LINKER:-}"
component_catalog="${repository_dir}/licenses/managed-components.json"
desktop_project="${repository_dir}/src/Asura.Desktop/Asura.Desktop.csproj"
cef_runtime_catalog="${repository_dir}/licenses/cef-runtime-components.json"
native_component_catalog="${repository_dir}/licenses/native-terminal-components.json"
font_assets_catalog="${repository_dir}/licenses/terminal-font-assets.json"
font_assets_directory="${repository_dir}/native/artifacts/common/fonts/JetBrainsMono"
font_assets_build_receipt="${repository_dir}/native/artifacts/common/terminal-font-assets-build-receipt.json"
product_identity_manifest="${repository_dir}/assets/macos/product-identity.json"
compile_macos_app_icon="${repository_dir}/scripts/compile-macos-app-icon.sh"
declare_macos_sdk="${repository_dir}/scripts/declare-macos-sdk26.sh"
sign_notarize_macos="${repository_dir}/scripts/sign-notarize-macos.sh"
namespace_avalonia_native="${repository_dir}/scripts/namespace-avalonia-native-macos.sh"
nuget_packages="${NUGET_PACKAGES:-${repository_dir}/.nuget/packages}"
sql_language_artifact_directory="${repository_dir}/native/artifacts/osx-arm64"
sql_language_worker="${sql_language_artifact_directory}/asura-sql-language"
sql_language_receipt="${sql_language_artifact_directory}/build-receipt.json"
workspace_gateway_host_directory="${repository_dir}/native/artifacts/osx-arm64"
workspace_gateway_host_name="asura-workspace-gateway-darwin-arm64"
connection_engine_directory="${repository_dir}/native/artifacts/osx-arm64/connection-engines"
workspace_runtime_directory="${repository_dir}/native/artifacts/osx-arm64/workspace-runtime"
connection_engine_code_files=(
    asura-openvpn-engine
    openconnect
    libopenconnect.5.dylib
    tailscale
    tailscaled
)
connection_engine_legal_files=(
    MANIFEST.sha256
    THIRD-PARTY-NOTICES.md
    GO-LICENSE.txt
    OPENCONNECT-LGPL-2.1.txt
    OPENCONNECT-SOURCE-AND-RELINKING.md
    OPENSSL-LICENSE.txt
    OPENVPN-MPL-2.0.txt
    OPENVPN-LICENSE.md
    ASIO-LICENSE.txt
    LZ4-LICENSE.txt
    OPENVPN-VERSIONS.txt
    OPENVPN-THIRD-PARTY-NOTICES.txt
)
connection_engine_source="sources/openconnect-9.21.tar.gz"

# Assets and their legal evidence move to separate bundle roots; compare each
# staged byte against the verified build payload before release signing.
verify_workspace_runtime_copy() {
    local code_directory="$1" legal_directory="$2" resource_directory="${3:-$1}"
    local source relative target
    while IFS= read -r source; do
        relative="${source#${workspace_runtime_directory}/}"
        case "${relative}" in
            kernel.bin|initfs.ext4|legal/sources/*) continue ;;
        esac
        if [[ "${relative}" == legal/* ]]; then
            target="${legal_directory}/${relative#legal/}"
        elif [[ "${relative}" == workspace-runtime || "${relative}" == *.dylib ]]; then
            target="${code_directory}/${relative}"
        else
            target="${resource_directory}/${relative}"
        fi
        if [[ ! -f "${target}" || -L "${target}" ]] || ! /usr/bin/cmp -s "${source}" "${target}"; then
            echo "The workspace runtime payload is missing or altered: ${relative}." >&2
            exit 1
        fi
    done < <(find "${workspace_runtime_directory}" -type f -print)
    [[ -x "${code_directory}/workspace-runtime" ]] || { echo "Workspace runtime is not executable." >&2; exit 1; }
    for excluded in "${resource_directory}/kernel.bin" "${resource_directory}/initfs.ext4" "${legal_directory}/sources"; do
        [[ ! -e "${excluded}" ]] || { echo "On-demand assets leaked into the app: ${excluded}" >&2; exit 1; }
    done
}
maven_content_lock="${repository_dir}/native/sql-language-worker/maven-content-lock.json"
maximum_sql_language_macos_version="13.0"

normalize_macos_version() {
    /usr/bin/awk -v version="$1" '
        BEGIN {
            if (version !~ /^[0-9]+([.][0-9]+)?([.][0-9]+)?$/) {
                exit 1
            }
            count = split(version, components, ".")
            major = components[1] + 0
            minor = count >= 2 ? components[2] + 0 : 0
            patch = count >= 3 ? components[3] + 0 : 0
            printf "%d.%d.%d\n", major, minor, patch
        }
    '
}

macos_version_is_at_most() {
    /usr/bin/awk -v candidate="$1" -v maximum="$2" '
        BEGIN {
            split(candidate, candidate_components, ".")
            split(maximum, maximum_components, ".")
            for (position = 1; position <= 3; position++) {
                candidate_component = candidate_components[position] + 0
                maximum_component = maximum_components[position] + 0
                if (candidate_component < maximum_component) {
                    exit 0
                }
                if (candidate_component > maximum_component) {
                    exit 1
                }
            }
            exit 0
        }
    '
}

usage() {
    cat >&2 <<'EOF'
Usage:
  ./scripts/package-macos.sh \
    --version <major.minor.patch> \
    --build-version <number[.number...]> \
    --output <path/to/Asura.app> \
    [--runtime-identifier osx-arm64] \
    [--cef-runtime-root <verified-runtime-directory>] \
    [--configuration Release] \
    [--sign-identity <Developer ID Application identity>] \
    [--notary-profile <notarytool keychain profile>] \
    [--release-evidence-dir <outside-app-directory>] \
    [--source-seal <outside-source-seal-directory>] \
    [--security-campaign-tool <outside-source-tool-dll>] \
    [--build-artifacts-root <outside-source-directory>]

Creates a speed-optimized Native AOT macOS arm64 release candidate. Without
--sign-identity the candidate receives a complete ad-hoc seal for local use.
Browser distribution requires a Developer ID identity and notarization.
--notary-profile requires Developer ID signing. A sealed release build may use
--release-evidence-dir before final notarization so a later release packager can
add its notarization record. The destination
must not already exist. The script never launches the application. Standalone
CEF runtime builds retain separate osx-x64 support; the full application does
not until its managed catalog and libghostty-vt receipt exist for that RID.
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
        --output)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            output="$2"
            shift 2
            ;;
        --configuration)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            configuration="$2"
            shift 2
            ;;
        --runtime-identifier)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            runtime_identifier="$2"
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

if [[ -z "${version}" || -z "${build_version}" || -z "${output}" ]]; then
    usage
    exit 64
fi

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "The macOS release candidate requires a macOS host." >&2
    exit 1
fi

if [[ -z "${runtime_identifier}" ]]; then
    runtime_identifier="osx-arm64"
fi

if [[ "${runtime_identifier}" != "osx-arm64" ]]; then
    echo "Full macOS application packaging currently supports only osx-arm64; osx-x64 lacks a reviewed managed catalog and libghostty-vt receipt." >&2
    exit 64
fi
expected_macho_architecture="arm64"
runtime_lock="${repository_dir}/src/Asura.Desktop/packages.${runtime_identifier}.lock.json"
native_aot_runtime_lock="${repository_dir}/src/Asura.Desktop/packages.${runtime_identifier}.aot.lock.json"
if [[ ! -f "${runtime_lock}" ]]; then
    echo "The reviewed ${runtime_identifier} dependency lock is missing: ${runtime_lock}." >&2
    exit 1
fi
if [[ ! -f "${native_aot_runtime_lock}" ]]; then
    echo "The reviewed ${runtime_identifier} Native AOT dependency lock is missing: ${native_aot_runtime_lock}." >&2
    exit 1
fi

native_artifact_directory="${repository_dir}/native/artifacts/${runtime_identifier}"
native_build_receipt="${native_artifact_directory}/native-terminal-build-receipt.json"
if [[ -z "${cef_runtime_root}" ]]; then
    cef_runtime_root="${native_artifact_directory}/cef"
fi

if [[ -n "${notary_profile}" && -z "${sign_identity}" ]]; then
    echo "--notary-profile requires --sign-identity." >&2
    exit 64
fi
if [[ -n "${notary_profile}" && -z "${release_evidence_dir}" ]]; then
    echo "Notarized packaging requires --release-evidence-dir." >&2
    exit 64
fi
if [[ -n "${source_seal}" && -z "${release_evidence_dir}" ]]; then
    echo "A sealed release build requires --release-evidence-dir." >&2
    exit 64
fi
if [[ -n "${release_evidence_dir}" && -z "${source_seal}" ]]; then
    echo "--release-evidence-dir requires a sealed release source." >&2
    exit 64
fi
if [[ -n "${release_evidence_dir}" \
    && ( -z "${source_seal}" \
        || -z "${security_campaign_tool}" \
        || -z "${build_artifacts_root}" \
        || -z "${ASURA_RELEASE_SOURCE_COMMIT:-}" \
        || -z "${ASURA_RELEASE_SOURCE_TREE:-}" \
        || -z "${ASURA_RELEASE_SOURCE_TAG:-}" ) ]]; then
    echo "Release evidence requires the sealed source, verifier, external build root, and exact source identity." >&2
    exit 64
fi
if [[ -n "${release_evidence_dir}" ]]; then
    if [[ -e "${release_evidence_dir}" ]]; then
        echo "The release evidence directory must not already exist." >&2
        exit 1
    fi
    mkdir -p "${release_evidence_dir}"
    release_evidence_dir="$(cd -- "${release_evidence_dir}" && pwd -P)"
fi

dotnet_artifacts_arguments=()
if [[ -n "${build_artifacts_root}" ]]; then
    mkdir -p "${build_artifacts_root}"
    build_artifacts_root="$(cd -- "${build_artifacts_root}" && pwd -P)"
    if [[ "${build_artifacts_root}" == "${repository_dir}" \
        || "${build_artifacts_root}" == "${repository_dir}/"* ]]; then
        echo "Release build artifacts must remain outside the sealed source root." >&2
        exit 64
    fi
    dotnet_artifacts_arguments=(--artifacts-path "${build_artifacts_root}")
fi

build_identity=""
verify_release_source() {
    local identity_arguments=()
    if [[ "${1:-}" == "--capture-build-identity" ]]; then
        if [[ -z "${release_evidence_dir}" ]]; then
            echo "A release build identity requires an external evidence directory." >&2
            exit 1
        fi
        build_identity="${release_evidence_dir}/release-build-identity.json"
        identity_arguments=(--build-identity-output "${build_identity}")
    fi
    "${dotnet}" "${security_campaign_tool}" \
        verify-release-source \
        --source-root "${repository_dir}" \
        --source-seal "${source_seal}" \
        --source-commit "${ASURA_RELEASE_SOURCE_COMMIT}" \
        --source-tree "${ASURA_RELEASE_SOURCE_TREE}" \
        --tag "${ASURA_RELEASE_SOURCE_TAG}" \
        ${identity_arguments[@]+"${identity_arguments[@]}"}
}

if [[ ! -x "${dotnet}" ]]; then
    echo "Run ./scripts/bootstrap.sh before packaging Asura." >&2
    exit 1
fi

if [[ -n "${source_seal}" ]]; then
    if [[ ! -d "${source_seal}" || ! -f "${security_campaign_tool}" ]]; then
        echo "The release source seal or its verifier is unavailable." >&2
        exit 1
    fi
    source_seal="$(cd -- "${source_seal}" && pwd -P)"
    security_campaign_tool="$(cd -- "$(dirname -- "${security_campaign_tool}")" && pwd -P)/$(basename -- "${security_campaign_tool}")"
    verify_release_source
fi

if [[ -z "${native_aot_linker}" ]]; then
    native_aot_linker="$(command -v ld64.lld || true)"
fi
if [[ -z "${native_aot_linker}" || ! -x "${native_aot_linker}" ]]; then
    echo "Native AOT packaging requires LLVM's ld64.lld. Set ASURA_NATIVE_AOT_LINKER to its absolute path." >&2
    exit 1
fi
native_aot_linker_version="$("${native_aot_linker}" --version)"
if [[ ! "${native_aot_linker_version}" =~ LLD[[:space:]]22\. ]]; then
    echo "Native AOT packaging requires LLVM lld 22.x; found ${native_aot_linker_version}." >&2
    exit 1
fi

if [[ ! -x "${declare_macos_sdk}" ]]; then
    echo "The macOS SDK declaration helper is unavailable." >&2
    exit 1
fi

if [[ -n "${sign_identity}" && ! -x "${sign_notarize_macos}" ]]; then
    echo "The macOS signing helper is unavailable." >&2
    exit 1
fi
if [[ ! -x "${namespace_avalonia_native}" ]]; then
    echo "The Avalonia Native Objective-C namespace helper is unavailable." >&2
    exit 1
fi
if [[ ! -x "${compile_macos_app_icon}" ]]; then
    echo "The Xcode application-icon compiler is unavailable." >&2
    exit 1
fi
if [[ ! -f "${product_identity_manifest}" || -L "${product_identity_manifest}" ]]; then
    echo "The reviewed macOS product identity manifest is unavailable." >&2
    exit 1
fi

if [[ "$(basename "${output}")" != "Asura.app" ]]; then
    echo "The --output path must end in Asura.app." >&2
    exit 64
fi

output_parent="$(dirname "${output}")"
if [[ ! -d "${output_parent}" ]]; then
    echo "The package destination parent does not exist." >&2
    exit 1
fi
output_parent="$(cd "${output_parent}" && pwd -P)"
output="${output_parent}/Asura.app"
symbol_output="${output_parent}/Asura.dSYM"

if [[ -e "${output}" ]]; then
    echo "The package destination already exists and will not be overwritten." >&2
    exit 1
fi
if [[ -e "${symbol_output}" ]]; then
    echo "The debug-symbol destination already exists and will not be overwritten." >&2
    exit 1
fi

required_native=(
    "${native_artifact_directory}/libghostty-vt.dylib"
    "${native_artifact_directory}/GHOSTTY-LICENSE"
    "${native_artifact_directory}/ghostty-vt-required-exports.txt"
    "${sql_language_worker}"
    "${sql_language_artifact_directory}/THIRD-PARTY-NOTICES.md"
    "${sql_language_artifact_directory}/runtime-dependencies.txt"
    "${sql_language_receipt}"
    "${workspace_gateway_host_directory}/${workspace_gateway_host_name}"
    "${workspace_gateway_host_directory}/workspace-network-gateway-MANIFEST.sha256"
    "${workspace_gateway_host_directory}/workspace-network-gateway-THIRD-PARTY-NOTICES.md"
    "${workspace_gateway_host_directory}/workspace-network-gateway-GO-LICENSE.txt"
    "${maven_content_lock}"
    "${native_component_catalog}"
    "${native_build_receipt}"
    "${font_assets_catalog}"
    "${font_assets_build_receipt}"
    "${font_assets_directory}/JetBrainsMono-Regular.ttf"
    "${font_assets_directory}/JetBrainsMono-Bold.ttf"
    "${font_assets_directory}/JetBrainsMono-Italic.ttf"
    "${font_assets_directory}/JetBrainsMono-BoldItalic.ttf"
    "${font_assets_directory}/OFL.txt"
    "${font_assets_directory}/MANIFEST.sha256"
)
for required in "${required_native[@]}"; do
    if [[ ! -e "${required}" ]]; then
        echo "The pinned native payload is incomplete; missing $(basename "${required}")." >&2
        exit 1
    fi
done
"${repository_dir}/scripts/build-workspace-runtime.sh" --verify
"${repository_dir}/scripts/build-workspace-backend.sh" --verify
ASURA_BACKEND_ARCH=x64 "${repository_dir}/scripts/build-workspace-backend.sh" --verify
for required in \
    "${connection_engine_code_files[@]}" \
    "${connection_engine_legal_files[@]}" \
    "${connection_engine_source}"; do
    if [[ ! -f "${connection_engine_directory}/${required}" \
        || -L "${connection_engine_directory}/${required}" ]]; then
        echo "The bundled connection engine payload is incomplete or linked; missing ${required}." >&2
        exit 1
    fi
done
(
    cd "${connection_engine_directory}"
    /usr/bin/shasum -a 256 -c MANIFEST.sha256
)

(
    cd "${workspace_gateway_host_directory}"
    /usr/bin/shasum -a 256 -c workspace-network-gateway-MANIFEST.sha256
)

sql_language_receipt_rid="$(/usr/bin/plutil -extract rid raw -o - "${sql_language_receipt}")"
sql_language_receipt_protocol="$(/usr/bin/plutil -extract protocolVersion raw -o - "${sql_language_receipt}")"
sql_language_receipt_artifact="$(/usr/bin/plutil -extract artifact raw -o - "${sql_language_receipt}")"
sql_language_receipt_abi="$(/usr/bin/plutil -extract abi raw -o - "${sql_language_receipt}")"
sql_language_receipt_minos="$(/usr/bin/plutil -extract minimumOsVersion raw -o - "${sql_language_receipt}")"
sql_language_expected_sha="$(/usr/bin/plutil -extract sha256 raw -o - "${sql_language_receipt}")"
sql_language_legal_closure_format="$(/usr/bin/plutil -extract legalClosureFormatVersion raw -expect integer -o - "${sql_language_receipt}")"
sql_language_legal_document_count="$(/usr/bin/plutil -extract legalDocumentCount raw -expect integer -o - "${sql_language_receipt}")"
sql_language_legal_review_required_count="$(/usr/bin/plutil -extract legalReviewRequiredCount raw -expect integer -o - "${sql_language_receipt}")"
sql_language_runtime_dependency_count="$(/usr/bin/plutil -extract runtimeDependencyCount raw -expect integer -o - "${sql_language_receipt}")"
sql_language_expected_dependencies_sha="$(/usr/bin/plutil -extract runtimeDependenciesSha256 raw -expect string -o - "${sql_language_receipt}")"
sql_language_expected_notices_sha="$(/usr/bin/plutil -extract thirdPartyNoticesSha256 raw -expect string -o - "${sql_language_receipt}")"
sql_language_expected_maven_lock_sha="$(/usr/bin/plutil -extract mavenContentLockSha256 raw -expect string -o - "${sql_language_receipt}")"
sql_language_actual_sha="$(/usr/bin/shasum -a 256 "${sql_language_worker}" | /usr/bin/awk '{print $1}')"
sql_language_actual_dependencies_sha="$(/usr/bin/shasum -a 256 "${sql_language_artifact_directory}/runtime-dependencies.txt" | /usr/bin/awk '{print $1}')"
sql_language_actual_notices_sha="$(/usr/bin/shasum -a 256 "${sql_language_artifact_directory}/THIRD-PARTY-NOTICES.md" | /usr/bin/awk '{print $1}')"
sql_language_actual_maven_lock_sha="$(/usr/bin/shasum -a 256 "${maven_content_lock}" | /usr/bin/awk '{print $1}')"
sql_language_file_type="$(/usr/bin/file -b "${sql_language_worker}")"
if [[ "${sql_language_receipt_rid}" != "osx-arm64" \
    || "${sql_language_receipt_protocol}" != "1" \
    || "${sql_language_receipt_artifact}" != "asura-sql-language" \
    || "${sql_language_receipt_abi}" != "darwin-arm64" \
    || "${sql_language_expected_sha}" != "${sql_language_actual_sha}" \
    || "${sql_language_file_type}" != *"Mach-O 64-bit executable arm64"* ]]; then
    echo "The SQL language worker does not match its osx-arm64 build receipt." >&2
    exit 1
fi
if [[ ! "${sql_language_legal_closure_format}" =~ ^[0-9]+$ \
    || ! "${sql_language_legal_document_count}" =~ ^[0-9]+$ \
    || ! "${sql_language_legal_review_required_count}" =~ ^[0-9]+$ \
    || ! "${sql_language_runtime_dependency_count}" =~ ^[0-9]+$ \
    || "${sql_language_legal_closure_format}" -lt 1 \
    || "${sql_language_legal_document_count}" -lt 1 \
    || "${sql_language_runtime_dependency_count}" -lt 1 ]]; then
    echo "The SQL language worker receipt has invalid legal closure counts." >&2
    exit 1
fi
if [[ "${sql_language_expected_dependencies_sha}" != "${sql_language_actual_dependencies_sha}" \
    || "${sql_language_expected_notices_sha}" != "${sql_language_actual_notices_sha}" \
    || "${sql_language_expected_maven_lock_sha}" != "${sql_language_actual_maven_lock_sha}" ]]; then
    echo "The SQL language worker legal or Maven-lock inputs do not match its build receipt." >&2
    exit 1
fi

sql_language_build_version_count="$(
    /usr/bin/otool -l "${sql_language_worker}" \
        | /usr/bin/awk '$1 == "cmd" && $2 == "LC_BUILD_VERSION" { count++ } END { print count + 0 }'
)"
sql_language_minos="$(
    /usr/bin/otool -l "${sql_language_worker}" \
        | /usr/bin/awk '
            $1 == "cmd" && $2 == "LC_BUILD_VERSION" { in_build_version = 1; next }
            in_build_version && $1 == "minos" { print $2; in_build_version = 0 }
        '
)"
sql_language_platform="$(
    /usr/bin/otool -l "${sql_language_worker}" \
        | /usr/bin/awk '
            $1 == "cmd" && $2 == "LC_BUILD_VERSION" { in_build_version = 1; next }
            in_build_version && $1 == "platform" { print $2; in_build_version = 0 }
        '
)"
if [[ "${sql_language_build_version_count}" != "1" \
    || -z "${sql_language_minos}" \
    || ( "${sql_language_platform}" != "1" && "${sql_language_platform}" != "MACOS" ) ]]; then
    echo "The SQL language worker must contain exactly one LC_BUILD_VERSION command." >&2
    exit 1
fi

if ! sql_language_receipt_minos_normalized="$(normalize_macos_version "${sql_language_receipt_minos}")" \
    || ! sql_language_minos_normalized="$(normalize_macos_version "${sql_language_minos}")" \
    || ! maximum_sql_language_macos_version_normalized="$(normalize_macos_version "${maximum_sql_language_macos_version}")"; then
    echo "The SQL language worker has a malformed macOS compatibility version." >&2
    exit 1
fi
if [[ "${sql_language_receipt_minos_normalized}" != "${sql_language_minos_normalized}" ]]; then
    echo "The SQL language worker LC_BUILD_VERSION does not match its build receipt." >&2
    exit 1
fi
if ! macos_version_is_at_most \
    "${sql_language_minos_normalized}" \
    "${maximum_sql_language_macos_version_normalized}"; then
    echo "The SQL language worker requires macOS ${sql_language_minos}, newer than Asura's macOS ${maximum_sql_language_macos_version} minimum." >&2
    exit 1
fi

if [[ ! -d "${cef_runtime_root}" ]]; then
    echo "The verified CEF runtime root does not exist: ${cef_runtime_root}" >&2
    exit 1
fi
if [[ ! -f "${cef_runtime_catalog}" ]]; then
    echo "The reviewed CEF runtime catalog is unavailable." >&2
    exit 1
fi

"${dotnet}" run \
    --project "${repository_dir}/tools/Asura.Packaging/Asura.Packaging.csproj" \
    --configuration Release \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    -- \
    cef-runtime-validate \
    --runtime-root "${cef_runtime_root}" \
    --catalog "${cef_runtime_catalog}" \
    --runtime-identifier "${runtime_identifier}"

cef_framework="${cef_runtime_root}/Chromium Embedded Framework.framework"
cef_macho_files=(
    "${cef_runtime_root}/libexclr8cef.dylib"
    "${cef_framework}/Chromium Embedded Framework"
    "${cef_framework}/Libraries/libEGL.dylib"
    "${cef_framework}/Libraries/libGLESv2.dylib"
    "${cef_framework}/Libraries/libcef_sandbox.dylib"
    "${cef_framework}/Libraries/libvk_swiftshader.dylib"
    "${cef_runtime_root}/Asura Helper.app/Contents/MacOS/Asura Helper"
    "${cef_runtime_root}/Asura Helper (Alerts).app/Contents/MacOS/Asura Helper (Alerts)"
    "${cef_runtime_root}/Asura Helper (GPU).app/Contents/MacOS/Asura Helper (GPU)"
    "${cef_runtime_root}/Asura Helper (Plugin).app/Contents/MacOS/Asura Helper (Plugin)"
    "${cef_runtime_root}/Asura Helper (Renderer).app/Contents/MacOS/Asura Helper (Renderer)"
)
for cef_macho in "${cef_macho_files[@]}"; do
    cef_file_description="$(/usr/bin/file -b "${cef_macho}")"
    if [[ "${cef_file_description}" != *"Mach-O 64-bit"* \
            || "${cef_file_description}" != *"${expected_macho_architecture}"* ]]; then
        echo "CEF runtime file $(basename "${cef_macho}") has the wrong macOS architecture." >&2
        exit 1
    fi
done

working_dir="$(mktemp -d "${TMPDIR:-/tmp}/asura-package-macos.XXXXXX")"
candidate_parent="$(mktemp -d "${output_parent}/.asura-package.XXXXXX")"
candidate="${candidate_parent}/Asura.app"
cleanup() {
    rm -rf -- "${working_dir}"
    rm -rf -- "${candidate_parent}"
}
trap cleanup EXIT

publish_dir="${working_dir}/publish"
managed_evidence_dir="${working_dir}/managed-evidence"
ASURA_DOTNET="${dotnet}" ASURA_NATIVE_AOT_LINKER="${native_aot_linker}" \
    "${script_dir}/check-browser-storage-aot.sh" "${working_dir}/browser-storage-acceptance"
aot_publish_log="${working_dir}/native-aot-publish.log"
compiled_icon_directory="${working_dir}/compiled-app-icon"
mkdir "${compiled_icon_directory}"
"${compile_macos_app_icon}" --output "${compiled_icon_directory}"
if [[ -n "${source_seal}" ]]; then
    verify_release_source
fi
"${dotnet}" restore \
    "${desktop_project}" \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    -maxcpucount:4 \
    --runtime "${runtime_identifier}" \
    --locked-mode \
    -p:AsuraProductVersion="${version}" \
    -p:AsuraProductionBuild=true \
    -p:AsuraMacReleaseNativeAot=true
if [[ -n "${source_seal}" ]]; then
    verify_release_source
    verify_release_source --capture-build-identity
    source_manifest_sha="$(/usr/bin/plutil -extract sealedManifestSha256 raw "${build_identity}")"
    if [[ ! "${source_manifest_sha}" =~ ^[0-9a-f]{64}$ ]]; then
        echo "The release build identity contains an invalid source manifest digest." >&2
        exit 1
    fi
fi
"${dotnet}" publish \
    "${desktop_project}" \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    -maxcpucount:4 \
    --configuration "${configuration}" \
    --runtime "${runtime_identifier}" \
    --self-contained true \
    --no-restore \
    --output "${managed_evidence_dir}" \
    -p:RestoreLockedMode=true \
    -p:AsuraProductVersion="${version}" \
    -p:AsuraProductionBuild=true \
    -p:AsuraReleaseSourceManifestSha256="${source_manifest_sha:-}" \
    -p:AsuraCefRuntimeArtifactDirectory="${cef_runtime_root}" \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:AsuraSqlLanguageRequired=true \
    -p:AsuraWorkspaceGatewayRequired=true \
    -p:AsuraConnectionEnginesRequired=true \
    -p:AsuraWorkspaceRuntimeRequired=true
if [[ -n "${source_seal}" ]]; then
    verify_release_source
fi
"${dotnet}" publish \
    "${desktop_project}" \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    -maxcpucount:4 \
    --configuration "${configuration}" \
    --runtime "${runtime_identifier}" \
    --self-contained true \
    --no-restore \
    --output "${publish_dir}" \
    -p:RestoreLockedMode=true \
    -p:AsuraMacReleaseNativeAot=true \
    -p:AsuraNativeAotLinker="${native_aot_linker}" \
    -p:AsuraProductVersion="${version}" \
    -p:AsuraProductionBuild=true \
    -p:AsuraReleaseSourceManifestSha256="${source_manifest_sha:-}" \
    -p:AsuraCefRuntimeArtifactDirectory="${cef_runtime_root}" \
    -p:DebugType=None \
    -p:DebugSymbols=false \
    -p:AsuraSqlLanguageRequired=true \
    -p:AsuraWorkspaceGatewayRequired=true \
    -p:AsuraConnectionEnginesRequired=true \
    -p:AsuraWorkspaceRuntimeRequired=true \
    2>&1 | /usr/bin/tee "${aot_publish_log}"
if [[ -n "${source_seal}" ]]; then
    verify_release_source
    if ! LC_ALL=C /usr/bin/grep -aFq "${source_manifest_sha}" "${publish_dir}/Asura"; then
        echo "The Native AOT executable does not embed the sealed source manifest identity." >&2
        exit 1
    fi
fi

if /usr/bin/grep -E \
    'ILC : (Trim analysis warning IL2026|AOT analysis warning IL3050): Asura\.' \
    "${aot_publish_log}"; then
    echo "Native AOT packaging rejected unsupported reflection in first-party code." >&2
    exit 1
fi

# Project-reference symbols are build artifacts, not release payload. Native
# AOT folds application IL into Asura, so no managed symbols are useful in
# the bundle.
find "${publish_dir}" -type f -name '*.pdb' -delete
published_symbols="${publish_dir}/Asura.dSYM"
staged_symbols="${working_dir}/Asura.dSYM"
if [[ ! -d "${published_symbols}" || -L "${published_symbols}" ]]; then
    echo "The Native AOT debug symbol bundle is missing or linked." >&2
    exit 1
fi
mv "${published_symbols}" "${staged_symbols}"

# Keep the runtime assets as a deterministic, independently receipted closure.
# Avalonia also embeds these faces for font discovery, but package provenance
# must remain inspectable without parsing a managed resource container.
publish_font_directory="${publish_dir}/fonts/JetBrainsMono"
mkdir -p "${publish_font_directory}"
for asset in \
    JetBrainsMono-Regular.ttf \
    JetBrainsMono-Bold.ttf \
    JetBrainsMono-Italic.ttf \
    JetBrainsMono-BoldItalic.ttf \
    OFL.txt \
    MANIFEST.sha256; do
    cp "${font_assets_directory}/${asset}" \
        "${publish_font_directory}/${asset}"
done
cp "${font_assets_catalog}" \
    "${publish_dir}/terminal-font-assets.json"
cp "${font_assets_build_receipt}" \
    "${publish_dir}/terminal-font-assets-build-receipt.json"
cp "${font_assets_directory}/OFL.txt" \
    "${publish_dir}/JETBRAINS-MONO-OFL.txt"

required_publish=(
    "${publish_dir}/Asura"
    "${publish_dir}/libAvaloniaNative.dylib"
    "${publish_dir}/libghostty-vt.dylib"
    "${publish_dir}/GHOSTTY-LICENSE"
    "${publish_dir}/ghostty-vt-required-exports.txt"
    "${publish_dir}/runtimes/osx-arm64/native/asura-sql-language"
    "${publish_dir}/runtimes/osx-arm64/native/THIRD-PARTY-NOTICES.md"
    "${publish_dir}/runtimes/osx-arm64/native/runtime-dependencies.txt"
    "${publish_dir}/runtimes/osx-arm64/native/build-receipt.json"
    "${publish_dir}/runtimes/osx-arm64/native/${workspace_gateway_host_name}"
    "${publish_dir}/runtimes/osx-arm64/native/workspace-network-gateway-MANIFEST.sha256"
    "${publish_dir}/runtimes/osx-arm64/native/workspace-network-gateway-THIRD-PARTY-NOTICES.md"
    "${publish_dir}/runtimes/osx-arm64/native/workspace-network-gateway-GO-LICENSE.txt"
    "${publish_dir}/THIRD-PARTY-NOTICES.md"
    "${publish_dir}/DOTNET-LICENSE.txt"
    "${publish_dir}/DOTNET-THIRD-PARTY-NOTICES.txt"
    "${publish_dir}/native-terminal-components.json"
    "${publish_dir}/native-terminal-build-receipt.json"
    "${publish_dir}/terminal-font-assets.json"
    "${publish_dir}/terminal-font-assets-build-receipt.json"
    "${publish_dir}/JETBRAINS-MONO-OFL.txt"
    "${publish_font_directory}/JetBrainsMono-Regular.ttf"
    "${publish_font_directory}/JetBrainsMono-Bold.ttf"
    "${publish_font_directory}/JetBrainsMono-Italic.ttf"
    "${publish_font_directory}/JetBrainsMono-BoldItalic.ttf"
    "${publish_font_directory}/OFL.txt"
    "${publish_font_directory}/MANIFEST.sha256"
    "${publish_dir}/ghostty/shell-integration/MANIFEST.sha256"
    "${publish_dir}/ghostty/shell-integration/bash/ghostty.bash"
    "${publish_dir}/ghostty/shell-integration/bash/bash-preexec.sh"
    "${publish_dir}/ghostty/shell-integration/fish/vendor_conf.d/ghostty-shell-integration.fish"
    "${publish_dir}/ghostty/shell-integration/zsh/.zshenv"
    "${publish_dir}/ghostty/shell-integration/zsh/ghostty-integration"
    "${publish_dir}/ghostty/shell-integration/SHELL-INTEGRATION-NOTICE.md"
)
for required in "${required_publish[@]}"; do
    if [[ ! -e "${required}" ]]; then
        echo "The Native AOT publish is incomplete; missing $(basename "${required}")." >&2
        exit 1
    fi
done

published_connection_engine_code="${publish_dir}/runtimes/osx-arm64/connection-engines"
verify_workspace_runtime_copy "${publish_dir}/runtimes/osx-arm64/workspace-runtime" "${publish_dir}/workspace-runtime-legal"
published_connection_engine_legal="${publish_dir}/connection-engine-legal"
for required in "${connection_engine_code_files[@]}"; do
    if [[ ! -f "${published_connection_engine_code}/${required}" \
        || -L "${published_connection_engine_code}/${required}" \
        || ! -x "${published_connection_engine_code}/${required}" ]] \
        || ! /usr/bin/cmp -s \
            "${connection_engine_directory}/${required}" \
            "${published_connection_engine_code}/${required}"; then
        echo "The published connection engine ${required} is missing, linked, non-executable, or altered." >&2
        exit 1
    fi
done
for required in "${connection_engine_legal_files[@]}"; do
    if [[ ! -f "${published_connection_engine_legal}/${required}" \
        || -L "${published_connection_engine_legal}/${required}" ]] \
        || ! /usr/bin/cmp -s \
            "${connection_engine_directory}/${required}" \
            "${published_connection_engine_legal}/${required}"; then
        echo "The published connection engine evidence ${required} is missing, linked, or altered." >&2
        exit 1
    fi
done

(
    cd "${publish_dir}/runtimes/osx-arm64/native"
    /usr/bin/shasum -a 256 -c workspace-network-gateway-MANIFEST.sha256
)

# Apply the Objective-C class namespace fix before managed evidence and package
# fingerprints are generated, so the inspected payload is the shipped payload.
"${namespace_avalonia_native}" \
    "${publish_dir}/libAvaloniaNative.dylib"
cp "${publish_dir}/libAvaloniaNative.dylib" \
    "${managed_evidence_dir}/libAvaloniaNative.dylib"

published_sql_language_worker="${publish_dir}/runtimes/osx-arm64/native/asura-sql-language"
published_sql_language_receipt="${publish_dir}/runtimes/osx-arm64/native/build-receipt.json"
published_sql_language_dependencies="${publish_dir}/runtimes/osx-arm64/native/runtime-dependencies.txt"
published_sql_language_notices="${publish_dir}/runtimes/osx-arm64/native/THIRD-PARTY-NOTICES.md"
published_sql_language_sha="$(/usr/bin/shasum -a 256 "${published_sql_language_worker}" | /usr/bin/awk '{print $1}')"
if [[ "${published_sql_language_sha}" != "${sql_language_expected_sha}" ]]; then
    echo "The published SQL language worker does not match its build receipt." >&2
    exit 1
fi
if ! /usr/bin/cmp -s "${sql_language_receipt}" "${published_sql_language_receipt}"; then
    echo "The published SQL language worker receipt differs from the verified receipt." >&2
    exit 1
fi
published_sql_language_dependencies_sha="$(/usr/bin/shasum -a 256 "${published_sql_language_dependencies}" | /usr/bin/awk '{print $1}')"
published_sql_language_notices_sha="$(/usr/bin/shasum -a 256 "${published_sql_language_notices}" | /usr/bin/awk '{print $1}')"
if [[ "${published_sql_language_dependencies_sha}" != "${sql_language_expected_dependencies_sha}" \
    || "${published_sql_language_notices_sha}" != "${sql_language_expected_notices_sha}" ]]; then
    echo "The published SQL language worker legal files do not match its build receipt." >&2
    exit 1
fi

# macOS uses the executable's SDK marker for compatibility styling. Rewrite the
# temporary Native AOT executable before any package fingerprint is produced; release signing
# replaces the helper's ad-hoc signature later.
"${declare_macos_sdk}" "${publish_dir}/Asura"

if find "${publish_dir}" -type f \
        \( -name '*.dll' -o -name '*.deps.json' -o -name '*.runtimeconfig.json' -o -name '*.pdb' \) \
        -print -quit | grep -q .; then
    echo "The Native AOT publish unexpectedly contains a managed host artifact." >&2
    exit 1
fi

first_party_assemblies=(
    "Asura.dll"
    "Asura.Agent.dll"
    "Asura.Agent.Providers.dll"
    "Asura.Agent.Runtime.dll"
    "Asura.App.dll"
    "Asura.Application.dll"
    "Asura.Browser.dll"
    "Asura.ConnectionBackend.dll"
    "Asura.Core.dll"
    "Asura.Databases.dll"
    "Asura.Docker.dll"
    "Asura.Docking.dll"
    "Asura.Files.dll"
    "Asura.Git.dll"
    "Asura.Infrastructure.dll"
    "Asura.Mcp.dll"
    "Asura.Mcp.Server.dll"
    "Asura.Monitoring.dll"
    "Asura.Previews.dll"
    "Asura.Protocol.dll"
    "Asura.Redis.dll"
    "Asura.SessionHost.dll"
    "Asura.Terminal.dll"
    "Asura.Updates.dll"
)
for assembly in "${first_party_assemblies[@]}"; do
    if [[ ! -f "${managed_evidence_dir}/${assembly}" ]]; then
        echo "The managed evidence publish is incomplete; missing ${assembly}." >&2
        exit 1
    fi

    if LC_ALL=C grep -aFq "${repository_dir}" "${managed_evidence_dir}/${assembly}"; then
        echo "${assembly} embeds the build host repository path." >&2
        exit 1
    fi
done

if find "${publish_dir}" ! -type f ! -type d -print -quit | grep -q .; then
    echo "The Native AOT publish contains a symbolic link or special entry." >&2
    exit 1
fi

if ! /usr/bin/file "${publish_dir}/Asura" \
        | grep -Eq "Mach-O 64-bit executable ${expected_macho_architecture}"; then
    echo "The published Asura executable has the wrong macOS architecture." >&2
    exit 1
fi

if ! /usr/bin/file "${publish_dir}/libghostty-vt.dylib" \
        | grep -Eq "Mach-O 64-bit dynamically linked shared library ${expected_macho_architecture}"; then
    echo "libghostty-vt.dylib has the wrong macOS architecture." >&2
    exit 1
fi
if ! /usr/bin/otool -D "${publish_dir}/libghostty-vt.dylib" \
        | grep -Fxq '@rpath/libghostty-vt.dylib'; then
    echo "libghostty-vt.dylib has an unexpected install name." >&2
    exit 1
fi
unexpected_dependencies="$(
    /usr/bin/otool -L "${publish_dir}/libghostty-vt.dylib" \
        | tail -n +2 \
        | awk '{print $1}' \
        | grep -Fvx '@rpath/libghostty-vt.dylib' \
        | grep -Fvx '/usr/lib/libSystem.B.dylib' \
        || true
)"
if [[ -n "${unexpected_dependencies}" ]]; then
    echo "libghostty-vt.dylib has an unexpected dynamic dependency." >&2
    exit 1
fi
unexpected_exports="$(
    /usr/bin/nm -gU "${publish_dir}/libghostty-vt.dylib" \
        | awk '$2 != "U" {print $3}' \
        | grep -Ev '^_ghostty_' \
        || true
)"
if [[ -n "${unexpected_exports}" ]]; then
    echo "libghostty-vt.dylib exports symbols outside the Ghostty C ABI." >&2
    exit 1
fi

if [[ -n "${source_seal}" ]]; then
    verify_release_source
fi
"${dotnet}" run \
    --project "${repository_dir}/tools/Asura.Packaging/Asura.Packaging.csproj" \
    --configuration Release \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    -- \
    macos \
    --publish "${publish_dir}" \
    --managed-evidence "${managed_evidence_dir}" \
    --output "${candidate}" \
    --version "${version}" \
    --build-version "${build_version}" \
    --product-identity-manifest "${product_identity_manifest}" \
    --product-identity-source-root "${repository_dir}" \
    --asset-catalog "${compiled_icon_directory}/Assets.car" \
    --component-catalog "${component_catalog}" \
    --native-component-catalog "${native_component_catalog}" \
    --native-build-receipt "${native_build_receipt}" \
    --font-assets-catalog "${font_assets_catalog}" \
    --font-assets-build-receipt "${font_assets_build_receipt}" \
    --nuget-packages "${nuget_packages}" \
    --cef-runtime-root "${cef_runtime_root}" \
    --cef-runtime-catalog "${cef_runtime_catalog}" \
    --runtime-identifier "${runtime_identifier}"

if [[ -n "${source_seal}" ]]; then
    verify_release_source
    release_identity_directory="${candidate}/Contents/Resources/Licenses/Release"
    mkdir -p "${release_identity_directory}"
    cp "${source_seal}/source-seal.json" \
        "${release_identity_directory}/source-seal.json"
    cp "${build_identity}" \
        "${release_identity_directory}/release-build-identity.json"
fi

/usr/bin/plutil -lint "${candidate}/Contents/Info.plist"
candidate_icon="${candidate}/Contents/Resources/Asura.icns"
candidate_asset_catalog="${candidate}/Contents/Resources/Assets.car"
candidate_identity="${candidate}/Contents/Resources/Licenses/ProductIdentity/product-identity.json"
if [[ ! -f "${candidate_icon}" || -L "${candidate_icon}" ]]; then
    echo "The packaged macOS application icon is missing or linked." >&2
    exit 1
fi
if [[ ! -f "${candidate_asset_catalog}" || -L "${candidate_asset_catalog}" ]]; then
    echo "The packaged Xcode asset catalog is missing or linked." >&2
    exit 1
fi
if ! /usr/bin/cmp -s "${product_identity_manifest}" "${candidate_identity}"; then
    echo "The packaged product identity differs from the reviewed manifest." >&2
    exit 1
fi
candidate_info_plist="${candidate}/Contents/Info.plist"
if [[ "$(/usr/bin/plutil -extract CFBundleDisplayName raw "${candidate_info_plist}")" != "Asura" \
    || "$(/usr/bin/plutil -extract CFBundleName raw "${candidate_info_plist}")" != "Asura" \
    || "$(/usr/bin/plutil -extract CFBundleExecutable raw "${candidate_info_plist}")" != "Asura" \
    || "$(/usr/bin/plutil -extract CFBundleIdentifier raw "${candidate_info_plist}")" != "sh.asura" \
    || "$(/usr/bin/plutil -extract CFBundleIconFile raw "${candidate_info_plist}")" != "Asura" \
    || "$(/usr/bin/plutil -extract CFBundleIconName raw "${candidate_info_plist}")" != "Asura" ]]; then
    echo "The packaged macOS application icon declaration is invalid." >&2
    exit 1
fi
candidate_asset_info="${working_dir}/candidate-Assets.info.json"
/usr/bin/assetutil --info "${candidate_asset_catalog}" > "${candidate_asset_info}"
if ! /usr/bin/grep -Fq '"AssetType" : "Icon Image"' "${candidate_asset_info}" \
    || ! /usr/bin/grep -Fq '"Name" : "Asura"' "${candidate_asset_info}"; then
    echo "The packaged Assets.car does not contain the named Asura icon." >&2
    exit 1
fi

if [[ ! -x "${candidate}/Contents/MacOS/Asura" ]]; then
    echo "The packaged Asura executable is not executable." >&2
    exit 1
fi
if [[ ! -x "${candidate}/Contents/MacOS/runtimes/osx-arm64/native/asura-sql-language" ]]; then
    echo "The packaged SQL language worker is missing or not executable." >&2
    exit 1
fi
candidate_workspace_gateway_host="${candidate}/Contents/MacOS/runtimes/osx-arm64/native"
if [[ ! -x "${candidate_workspace_gateway_host}/${workspace_gateway_host_name}" ]]; then
    echo "The packaged workspace network gateway payload is incomplete or not executable." >&2
    exit 1
fi
candidate_workspace_gateway_resources="${candidate}/Contents/Resources/runtimes/osx-arm64/native"
for required in "${workspace_gateway_host_name}" \
    workspace-network-gateway-MANIFEST.sha256 \
    workspace-network-gateway-THIRD-PARTY-NOTICES.md \
    workspace-network-gateway-GO-LICENSE.txt; do
    gateway_target="${candidate_workspace_gateway_resources}/${required}"
    if [[ "${required}" == "${workspace_gateway_host_name}" ]]; then
        gateway_target="${candidate_workspace_gateway_host}/${required}"
    fi
    if [[ ! -f "${gateway_target}" || -L "${gateway_target}" ]] \
        || ! /usr/bin/cmp -s "${workspace_gateway_host_directory}/${required}" "${gateway_target}"; then
        echo "The packaged workspace network gateway payload is missing or altered: ${required}." >&2
        exit 1
    fi
done
candidate_connection_engine_code="${candidate}/Contents/MacOS/runtimes/osx-arm64/connection-engines"
verify_workspace_runtime_copy "${candidate}/Contents/MacOS/runtimes/osx-arm64/workspace-runtime" "${candidate}/Contents/Resources/workspace-runtime-legal" "${candidate}/Contents/Resources/runtimes/osx-arm64/workspace-runtime"
candidate_connection_engine_legal="${candidate}/Contents/Resources/connection-engine-legal"
for required in "${connection_engine_code_files[@]}"; do
    if [[ ! -f "${candidate_connection_engine_code}/${required}" \
        || -L "${candidate_connection_engine_code}/${required}" \
        || ! -x "${candidate_connection_engine_code}/${required}" ]] \
        || ! /usr/bin/cmp -s \
            "${connection_engine_directory}/${required}" \
            "${candidate_connection_engine_code}/${required}"; then
        echo "The packaged connection engine ${required} is missing, linked, non-executable, or altered." >&2
        exit 1
    fi
done
for required in "${connection_engine_legal_files[@]}"; do
    if [[ ! -f "${candidate_connection_engine_legal}/${required}" \
        || -L "${candidate_connection_engine_legal}/${required}" ]] \
        || ! /usr/bin/cmp -s \
            "${connection_engine_directory}/${required}" \
            "${candidate_connection_engine_legal}/${required}"; then
        echo "The packaged connection engine evidence ${required} is missing, linked, or altered." >&2
        exit 1
    fi
done
candidate_sql_language_directory="${candidate}/Contents/MacOS/runtimes/osx-arm64/native"
candidate_sql_language_resources="${candidate}/Contents/Resources/Native/SqlLanguage"
for required in \
    THIRD-PARTY-NOTICES.md \
    runtime-dependencies.txt \
    build-receipt.json; do
    if [[ ! -f "${candidate_sql_language_resources}/${required}" ]]; then
        echo "The packaged SQL language worker metadata is incomplete; missing ${required}." >&2
        exit 1
    fi
done
candidate_sql_language_sha="$(/usr/bin/shasum -a 256 "${candidate_sql_language_directory}/asura-sql-language" | /usr/bin/awk '{print $1}')"
if [[ "${candidate_sql_language_sha}" != "${sql_language_expected_sha}" ]]; then
    echo "The packaged SQL language worker does not match its build receipt." >&2
    exit 1
fi
if ! /usr/bin/cmp -s \
    "${sql_language_receipt}" \
    "${candidate_sql_language_resources}/build-receipt.json"; then
    echo "The packaged SQL language worker receipt differs from the verified receipt." >&2
    exit 1
fi
candidate_sql_language_dependencies_sha="$(/usr/bin/shasum -a 256 "${candidate_sql_language_resources}/runtime-dependencies.txt" | /usr/bin/awk '{print $1}')"
candidate_sql_language_notices_sha="$(/usr/bin/shasum -a 256 "${candidate_sql_language_resources}/THIRD-PARTY-NOTICES.md" | /usr/bin/awk '{print $1}')"
if [[ "${candidate_sql_language_dependencies_sha}" != "${sql_language_expected_dependencies_sha}" \
    || "${candidate_sql_language_notices_sha}" != "${sql_language_expected_notices_sha}" ]]; then
    echo "The packaged SQL language worker legal files do not match its build receipt." >&2
    exit 1
fi

while IFS= read -r -d '' candidate_code_file; do
    if [[ "$(/usr/bin/file -b "${candidate_code_file}")" != Mach-O* ]]; then
        echo "Non-code payload was placed in Contents/MacOS: ${candidate_code_file#${candidate}/Contents/MacOS/}" >&2
        exit 1
    fi
done < <(find "${candidate}/Contents/MacOS" -type f -print0)

sign_arguments=(
    --app "${candidate}"
    --identity "${sign_identity:--}"
)
if [[ -n "${notary_profile}" ]]; then
    sign_arguments+=(--notary-profile "${notary_profile}")
    sign_arguments+=(--evidence "${release_evidence_dir}/notarization.json")
fi
"${sign_notarize_macos}" "${sign_arguments[@]}"

"${dotnet}" run \
    --project \
    "${repository_dir}/tools/Asura.AccessibilityAcceptance/Asura.AccessibilityAcceptance.csproj" \
    --configuration Release \
    ${dotnet_artifacts_arguments[@]+"${dotnet_artifacts_arguments[@]}"} \
    -- \
    publish-macos-package \
    --build-label "macos-${version}-${build_version}" \
    --package "${candidate}" \
    --output "${output}"

mv "${staged_symbols}" "${symbol_output}"

if [[ -n "${notary_profile}" ]]; then
    echo "Created signed and notarized macOS release candidate at ${output}."
elif [[ -n "${sign_identity}" ]]; then
    echo "Created signed macOS release candidate at ${output}."
else
    echo "Created ad-hoc signed macOS development candidate at ${output}."
fi
echo "Created matching debug symbols at ${symbol_output}."
