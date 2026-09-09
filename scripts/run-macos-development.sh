#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
namespace_avalonia_native="${repository_dir}/scripts/namespace-avalonia-native-macos.sh"
target_directory=""
cef_runtime_root=""
app_bundle=""
info_plist_template=""
app_icon="${repository_dir}/assets/macos/GhostShell.icns"
application_arguments=()
assemble_only=false

usage() {
    cat >&2 <<'EOF'
Usage: run-macos-development.sh \
  --target-directory <build-output> \
  --cef-runtime-root <cef-runtime> \
  --app <obj-path/GhostShell.dev.app> \
  --info-plist-template <template> \
  [--assemble-only] \
  [-- <GhostSHELL arguments>]

Assembles the framework-dependent build output into the macOS bundle layout
required by CEF, then replaces this process with the bundled executable.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
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
    echo "The GhostSHELL build output is missing or linked: ${target_directory}" >&2
    exit 1
fi
if [[ ! -d "${cef_runtime_root}" || -L "${cef_runtime_root}" ]]; then
    echo "The CEF runtime root is missing or linked: ${cef_runtime_root}" >&2
    exit 1
fi
if [[ ! -f "${info_plist_template}" || -L "${info_plist_template}" ]]; then
    echo "The GhostSHELL Info.plist template is missing or linked." >&2
    exit 1
fi
if [[ ! -f "${app_icon}" || -L "${app_icon}" ]]; then
    echo "The GhostSHELL macOS application icon is missing or linked." >&2
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
expected_app_prefix="${repository_dir}/src/GhostShell.Desktop/obj/"
case "${app_bundle}" in
    "${expected_app_prefix}"*"/GhostShell.dev.app") ;;
    *)
        echo "The development app must remain under GhostShell.Desktop/obj." >&2
        exit 1
        ;;
esac
if [[ -L "${app_bundle}" ]]; then
    echo "The development app destination must not be a symbolic link." >&2
    exit 1
fi

target_executable="${target_directory}/GhostShell"
if [[ ! -x "${target_executable}" \
        || ! -f "${target_directory}/GhostShell.dll" \
        || ! -f "${target_directory}/GhostShell.runtimeconfig.json" ]]; then
    echo "The GhostSHELL build output is incomplete." >&2
    exit 1
fi

required_cef_payload=(
    "libexclr8cef.dylib"
    "Chromium Embedded Framework.framework/Chromium Embedded Framework"
    "GhostSHELL Helper.app/Contents/MacOS/GhostSHELL Helper"
    "GhostSHELL Helper (Alerts).app/Contents/MacOS/GhostSHELL Helper (Alerts)"
    "GhostSHELL Helper (GPU).app/Contents/MacOS/GhostSHELL Helper (GPU)"
    "GhostSHELL Helper (Plugin).app/Contents/MacOS/GhostSHELL Helper (Plugin)"
    "GhostSHELL Helper (Renderer).app/Contents/MacOS/GhostSHELL Helper (Renderer)"
)
for required in "${required_cef_payload[@]}"; do
    if [[ ! -f "${cef_runtime_root}/${required}" ]]; then
        echo "The CEF development payload is incomplete; missing ${required}." >&2
        exit 1
    fi
done

candidate_parent="$(mktemp -d "${app_parent}/.ghostshell-macos-run.XXXXXX")"
candidate="${candidate_parent}/GhostShell.dev.app"
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
"${repository_dir}/scripts/build-macos-connection-engines.sh" --stage "${macos_directory}"
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
workspace_runtime="${macos_directory}/runtimes/osx-arm64/workspace-runtime/workspace-runtime"
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
        --entitlements "${repository_dir}/tools/GhostShell.Packaging/MacOS/WorkspaceRuntime.entitlements" \
        "${workspace_runtime}"
    /usr/bin/codesign --verify --strict "${workspace_runtime}"
fi
"${namespace_avalonia_native}" \
    "${macos_directory}/runtimes/osx/native/libAvaloniaNative.dylib"
/usr/bin/sed \
    -e 's/__GHOSTSHELL_VERSION__/0.0.0/g' \
    -e 's/__GHOSTSHELL_BUILD_VERSION__/1/g' \
    "${info_plist_template}" > "${contents}/Info.plist"
/usr/bin/plutil -lint "${contents}/Info.plist" >/dev/null
/usr/bin/plutil -replace CFBundleIdentifier -string app.ghostshell.development "${contents}/Info.plist"
/usr/bin/plutil -replace CFBundleName -string "GhostShell Development" "${contents}/Info.plist"
/usr/bin/plutil -replace CFBundleDisplayName -string "GhostShell Development" "${contents}/Info.plist"
/usr/bin/ditto --noqtn "${app_icon}" "${resources_directory}/GhostShell.icns"

/usr/bin/ditto --clone --noqtn \
    "${cef_runtime_root}/Chromium Embedded Framework.framework" \
    "${frameworks_directory}/Chromium Embedded Framework.framework"
for helper_name in \
    "GhostSHELL Helper" \
    "GhostSHELL Helper (Alerts)" \
    "GhostSHELL Helper (GPU)" \
    "GhostSHELL Helper (Plugin)" \
    "GhostSHELL Helper (Renderer)"; do
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
export GHOSTSHELL_WORKSPACE_BOOT_ARCHIVE="${repository_dir}/native/artifacts/workspace-runtime-build/distribution/GhostShell-workspace-boot-arm64.zip"
export GHOSTSHELL_WORKSPACE_BACKEND_ARCHIVE="${repository_dir}/native/artifacts/workspace-backend-build/distribution/GhostShell-workspace-backend-arm64.tar.gz"
export GHOSTSHELL_WORKSPACE_BACKEND_X64_ARCHIVE="${repository_dir}/native/artifacts/workspace-backend-build/distribution/x64/GhostShell-workspace-backend-x64.tar.gz"
if [[ ${#application_arguments[@]} -eq 0 ]]; then
    exec "${app_bundle}/Contents/MacOS/GhostShell"
fi
exec "${app_bundle}/Contents/MacOS/GhostShell" "${application_arguments[@]}"
