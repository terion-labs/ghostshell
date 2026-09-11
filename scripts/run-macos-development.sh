#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
namespace_avalonia_native="${repository_dir}/scripts/namespace-avalonia-native-macos.sh"
target_directory=""
cef_runtime_root=""
app_bundle=""
info_plist_template=""
app_icon="${repository_dir}/assets/macos/Asura.icns"
application_arguments=()
assemble_only=false
runtime_identifier=""

usage() {
    cat >&2 <<'EOF'
Usage: run-macos-development.sh \
  --target-directory <build-output> \
  --cef-runtime-root <cef-runtime> \
  --app <obj-path/Asura.dev.app> \
  --info-plist-template <template> \
  --runtime-identifier <osx-arm64|osx-x64> \
  [--assemble-only] \
  [-- <Asura arguments>]

Assembles the framework-dependent build output into the macOS bundle layout
required by CEF, then replaces this process with the bundled executable.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --runtime-identifier)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            runtime_identifier="$2"
            shift 2
            ;;
        --target-directory)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            target_directory="$2"
            shift 2
            ;;
        --cef-runtime-root)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            cef_runtime_root="$2"
            shift 2
            ;;
        --app)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            app_bundle="$2"
            shift 2
            ;;
        --info-plist-template)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            info_plist_template="$2"
            shift 2
            ;;
        --assemble-only)
            assemble_only=true
            shift
            ;;
        --)
            shift
            application_arguments=("$@")
            break
            ;;
        *)
            echo "Unknown option: $1" >&2
            usage
            exit 64
            ;;
    esac
done

case "${runtime_identifier}" in
    osx-arm64|osx-x64) ;;
    *) echo "A macOS target runtime identifier is required." >&2; usage; exit 64 ;;
esac

if [[ "$(uname -s)" != "Darwin" ]]; then
    echo "The macOS development-bundle runner requires macOS." >&2
    exit 1
fi
if [[ -z "${target_directory}" \
        || -z "${cef_runtime_root}" \
        || -z "${app_bundle}" \
        || -z "${info_plist_template}" ]]; then
    usage
    exit 64
fi
if [[ ! -d "${target_directory}" || -L "${target_directory}" ]]; then
    echo "The Asura build output is missing or linked: ${target_directory}" >&2
    exit 1
fi
if [[ ! -d "${cef_runtime_root}" || -L "${cef_runtime_root}" ]]; then
    echo "The CEF runtime root is missing or linked: ${cef_runtime_root}" >&2
    exit 1
fi
if [[ ! -f "${info_plist_template}" || -L "${info_plist_template}" ]]; then
    echo "The Asura Info.plist template is missing or linked." >&2
    exit 1
fi
if [[ ! -f "${app_icon}" || -L "${app_icon}" ]]; then
    echo "The Asura macOS application icon is missing or linked." >&2
    exit 1
fi
if [[ ! -x "${namespace_avalonia_native}" ]]; then
    echo "The Avalonia Native Objective-C namespace helper is unavailable." >&2
    exit 1
fi

target_directory="$(cd -- "${target_directory}" && pwd -P)"
cef_runtime_root="$(cd -- "${cef_runtime_root}" && pwd -P)"
info_plist_directory="$(cd -- "$(dirname -- "${info_plist_template}")" && pwd -P)"
info_plist_template="${info_plist_directory}/$(basename -- "${info_plist_template}")"

app_parent_input="$(dirname -- "${app_bundle}")"
mkdir -p -- "${app_parent_input}"
app_parent="$(cd -- "${app_parent_input}" && pwd -P)"
app_bundle="${app_parent}/$(basename -- "${app_bundle}")"
expected_app_prefix="${repository_dir}/src/Asura.Desktop/obj/"
case "${app_bundle}" in
    "${expected_app_prefix}"*"/Asura.dev.app") ;;
    *)
        echo "The development app must remain under Asura.Desktop/obj." >&2
        exit 1
        ;;
esac
if [[ -L "${app_bundle}" ]]; then
    echo "The development app destination must not be a symbolic link." >&2
    exit 1
fi

target_executable="${target_directory}/Asura"
if [[ ! -x "${target_executable}" \
        || ! -f "${target_directory}/Asura.dll" \
        || ! -f "${target_directory}/Asura.runtimeconfig.json" ]]; then
    echo "The Asura build output is incomplete." >&2
    exit 1
fi

required_cef_payload=(
    "libexclr8cef.dylib"
    "Chromium Embedded Framework.framework/Chromium Embedded Framework"
    "Asura Helper.app/Contents/MacOS/Asura Helper"
    "Asura Helper (Alerts).app/Contents/MacOS/Asura Helper (Alerts)"
    "Asura Helper (GPU).app/Contents/MacOS/Asura Helper (GPU)"
    "Asura Helper (Plugin).app/Contents/MacOS/Asura Helper (Plugin)"
    "Asura Helper (Renderer).app/Contents/MacOS/Asura Helper (Renderer)"
)
for required in "${required_cef_payload[@]}"; do
    if [[ ! -f "${cef_runtime_root}/${required}" ]]; then
        echo "The CEF development payload is incomplete; missing ${required}." >&2
        exit 1
    fi
done

candidate_parent="$(mktemp -d "${app_parent}/.asura-macos-run.XXXXXX")"
candidate="${candidate_parent}/Asura.dev.app"
trap 'rm -rf -- "${candidate_parent}"' EXIT

contents="${candidate}/Contents"
macos_directory="${contents}/MacOS"
frameworks_directory="${contents}/Frameworks"
resources_directory="${contents}/Resources"
mkdir -p -- "${macos_directory}" "${frameworks_directory}" "${resources_directory}"

echo "Assembling the macOS CEF development bundle..." >&2
if ! "${repository_dir}/scripts/build-workspace-backend.sh" --verify >/dev/null 2>&1; then
    "${repository_dir}/scripts/build-workspace-backend.sh"
fi
/usr/bin/ditto --clone --noqtn "${target_directory}" "${macos_directory}"
# MSBuild evaluates optional Content before provisioning. Copy from the verified
# cache explicitly, including on the first run with an empty managed output.
"${repository_dir}/scripts/build-macos-connection-engines.sh" --stage "${macos_directory}" --rid "${runtime_identifier}"
# Optional MSBuild content can be absent or stale before native provisioning.
# Stage the verified current runtime explicitly, just like the connection engines.
workspace_runtime="${macos_directory}/runtimes/osx-arm64/workspace-runtime/workspace-runtime"
if [[ "${runtime_identifier}" == osx-arm64 ]]; then
    runtime_cache="${repository_dir}/native/artifacts/osx-arm64/workspace-runtime"
    if ! "${repository_dir}/scripts/build-workspace-runtime.sh" --verify >/dev/null 2>&1; then
        echo "Restoring missing or invalid development workspace runtime..." >&2
        "${repository_dir}/scripts/build-workspace-runtime.sh"
    fi
    "${repository_dir}/scripts/build-workspace-runtime.sh" --verify >/dev/null
    runtime_destination="${workspace_runtime%/*}"
    legal_destination="${macos_directory}/workspace-runtime-legal"
    rm -rf -- "${runtime_destination}" "${legal_destination}"
    mkdir -p "${runtime_destination}" "${legal_destination}"
    for runtime_asset in "${runtime_cache}/"*; do
        case "${runtime_asset##*/}" in legal|kernel.bin|initfs.ext4) continue ;; esac
        /usr/bin/ditto --clone --noqtn "${runtime_asset}" "${runtime_destination}/${runtime_asset##*/}"
    done
    for legal_asset in "${runtime_cache}/legal/"*; do
        [[ "${legal_asset##*/}" == sources ]] && continue
        /usr/bin/ditto --clone --noqtn "${legal_asset}" "${legal_destination}/${legal_asset##*/}"
    done
fi
backend_resources="${resources_directory}/runtimes/linux-arm64/workspace-backend"
mkdir -p "${backend_resources}"
cp "${repository_dir}/native/artifacts/workspace-backend-build/distribution/backend-assets.json" "${backend_resources}/"
rm -f -- "${macos_directory}/runtimes/linux-arm64/workspace-backend/backend-assets.json"
if [[ -f "${repository_dir}/native/artifacts/workspace-backend-build/distribution/x64/backend-assets.json" ]]; then
    mkdir -p "${resources_directory}/runtimes/linux-x64/workspace-backend"
    cp "${repository_dir}/native/artifacts/workspace-backend-build/distribution/x64/backend-assets.json" "${resources_directory}/runtimes/linux-x64/workspace-backend/"
    rm -f -- "${macos_directory}/runtimes/linux-x64/workspace-backend/backend-assets.json"
fi
# Incremental managed output may still contain the retired in-guest helper.
# The SDK runtime never mounts application code into the guest.
rm -rf -- "${macos_directory}/runtimes/linux-arm64/guest"
# Incremental dotnet output can still contain files copied by older versions.
# These are disposable staging copies, never the shared provisioning cache.
rm -f -- "${workspace_runtime%/*}/kernel.bin" "${workspace_runtime%/*}/initfs.ext4"
rm -rf -- "${macos_directory}/workspace-runtime-legal/sources" "${macos_directory}/connection-engine-legal/sources"
if [[ -f "${workspace_runtime}" ]]; then
    # Match the release bundle's resource layout so SDK boot paths are identical.
    workspace_resources="${resources_directory}/runtimes/osx-arm64/workspace-runtime"
    mkdir -p "${workspace_resources}"
    for runtime_asset in "${workspace_runtime%/*}/"*; do
        case "${runtime_asset##*/}" in
            workspace-runtime|*.dylib) continue ;;
        esac
        mv "${runtime_asset}" "${workspace_resources}/"
    done
    # Re-sign this child only; Chromium's entitlements must not be applied to
    # the VM owner, and VM privileges must not spread to other app executables.
    /usr/bin/codesign --force --sign - \
        --entitlements "${repository_dir}/tools/Asura.Packaging/MacOS/WorkspaceRuntime.entitlements" \
        "${workspace_runtime}"
    /usr/bin/codesign --verify --strict "${workspace_runtime}"
fi
"${namespace_avalonia_native}" \
    "${macos_directory}/runtimes/osx/native/libAvaloniaNative.dylib"
/usr/bin/sed \
    -e 's/__ASURA_VERSION__/0.0.0/g' \
    -e 's/__ASURA_BUILD_VERSION__/1/g' \
    "${info_plist_template}" > "${contents}/Info.plist"
/usr/bin/plutil -lint "${contents}/Info.plist" >/dev/null
/usr/bin/plutil -replace CFBundleIdentifier -string sh.asura.development "${contents}/Info.plist"
/usr/bin/plutil -replace CFBundleName -string "Asura Development" "${contents}/Info.plist"
/usr/bin/plutil -replace CFBundleDisplayName -string "Asura Development" "${contents}/Info.plist"
/usr/bin/ditto --noqtn "${app_icon}" "${resources_directory}/Asura.icns"

/usr/bin/ditto --clone --noqtn \
    "${cef_runtime_root}/Chromium Embedded Framework.framework" \
    "${frameworks_directory}/Chromium Embedded Framework.framework"
python3 "${repository_dir}/scripts/scope-cef-keychain.py" \
    "${frameworks_directory}/Chromium Embedded Framework.framework/Chromium Embedded Framework" \
    --source release --target development
/usr/bin/codesign --force --sign - \
    "${frameworks_directory}/Chromium Embedded Framework.framework"
for helper_name in \
    "Asura Helper" \
    "Asura Helper (Alerts)" \
    "Asura Helper (GPU)" \
    "Asura Helper (Plugin)" \
    "Asura Helper (Renderer)"; do
    /usr/bin/ditto --clone --noqtn \
        "${cef_runtime_root}/${helper_name}.app" \
        "${frameworks_directory}/${helper_name}.app"
done
/usr/bin/ditto --clone --noqtn \
    "${cef_runtime_root}/libexclr8cef.dylib" \
    "${frameworks_directory}/libexclr8cef.dylib"
/usr/bin/ditto --clone --noqtn \
    "${cef_runtime_root}/libexclr8cef.dylib" \
    "${macos_directory}/libexclr8cef.dylib"

if [[ -e "${app_bundle}" ]]; then
    if [[ ! -d "${app_bundle}" ]]; then
        echo "The development app destination is not a directory." >&2
        exit 1
    fi
    rm -rf -- "${app_bundle}"
fi
mv -- "${candidate}" "${app_bundle}"
rmdir -- "${candidate_parent}"
trap - EXIT

if [[ "${assemble_only}" == true ]]; then
    echo "Assembled ${app_bundle}" >&2
    exit 0
fi
echo "Launching ${app_bundle}" >&2
# Development uses the same verified provisioning path without fetching an
# unpublished app version from GitHub. Do not copy the sidecar into the bundle.
export ASURA_WORKSPACE_BOOT_ARCHIVE="${repository_dir}/native/artifacts/workspace-runtime-build/distribution/Asura-workspace-boot-arm64.zip"
export ASURA_WORKSPACE_BACKEND_ARCHIVE="${repository_dir}/native/artifacts/workspace-backend-build/distribution/Asura-workspace-backend-arm64.tar.gz"
export ASURA_WORKSPACE_BACKEND_X64_ARCHIVE="${repository_dir}/native/artifacts/workspace-backend-build/distribution/x64/Asura-workspace-backend-x64.tar.gz"
if [[ ${#application_arguments[@]} -eq 0 ]]; then
    exec "${app_bundle}/Contents/MacOS/Asura"
fi
exec "${app_bundle}/Contents/MacOS/Asura" "${application_arguments[@]}"
