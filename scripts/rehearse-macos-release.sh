#!/usr/bin/env bash
set -euo pipefail

# Keep release credentials in this coordinating shell, not in scanner, build,
# or dependency-tool subprocess environments. Import them only at signing time.
export -n APPLE_CERTIFICATE_P12_BASE64 APPLE_CERTIFICATE_PASSWORD \
    APPLE_DEVELOPER_ID_APPLICATION APPLE_NOTARY_ISSUER_ID \
    APPLE_NOTARY_KEY_ID APPLE_NOTARY_PRIVATE_KEY_BASE64

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repository_dir="$(cd -- "${script_dir}/.." && pwd -P)"
dotnet="${repository_dir}/.dotnet/dotnet"
tag=""
build_version=""
reuse_pass=false

usage() {
    cat >&2 <<'EOF'
Usage: ./scripts/rehearse-macos-release.sh --tag v<major>.<minor>.<patch> [options]

Options:
  --build-version <number[.number...]>  Bundle build version (defaults to commit count).
  --reuse-pass                        Reuse a passing receipt for this exact tag commit.

Required release environment (the same names used by GitHub Actions):
  APPLE_CERTIFICATE_P12_BASE64
  APPLE_CERTIFICATE_PASSWORD
  APPLE_DEVELOPER_ID_APPLICATION
  APPLE_NOTARY_ISSUER_ID
  APPLE_NOTARY_KEY_ID
  APPLE_NOTARY_PRIVATE_KEY_BASE64

Required local toolchain:
  GRAALVM_HOME                 GraalVM 25.0.4 home containing native-image.

Optional toolchain overrides:
  GHOSTSHELL_XCODE_APP         Full Xcode 26 or newer application directory.
  GHOSTSHELL_NATIVE_AOT_LINKER Absolute path to LLVM ld64.lld 22.x.

This is the local equivalent of the tag release job. It runs the full repository
gate, builds from a read-only sealed source export, builds every native payload,
Developer-ID signs and notarizes the application, verifies its extracted ZIP,
and assembles and revalidates the exact release evidence. It never publishes.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --tag)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            tag="$2"
            shift 2
            ;;
        --build-version)
            [[ $# -ge 2 ]] || { usage; exit 64; }
            build_version="$2"
            shift 2
            ;;
        --reuse-pass)
            reuse_pass=true
            shift
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

if [[ ! "${tag}" =~ ^v[0-9]{1,9}\.[0-9]{1,9}\.[0-9]{1,9}$ ]]; then
    echo "A release tag in v<major>.<minor>.<patch> form is required." >&2
    exit 64
fi
if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
    echo "The macOS release rehearsal requires an Apple Silicon macOS host." >&2
    exit 1
fi
if [[ ! -x "${dotnet}" ]]; then
    echo "The pinned repository SDK is unavailable at ${dotnet}." >&2
    exit 1
fi

cd "${repository_dir}"
commit="$(git rev-parse "${tag}^{commit}" 2>/dev/null || true)"
head_commit="$(git rev-parse HEAD)"
if [[ -z "${commit}" || "${commit}" != "${head_commit}" ]]; then
    echo "Tag ${tag} must exist locally and resolve to the checked-out commit." >&2
    exit 1
fi
tree="$(git rev-parse "${commit}^{tree}")"
if [[ -n "$(git status --porcelain --untracked-files=no)" ]]; then
    echo "Tracked files must be clean before a release rehearsal." >&2
    exit 1
fi
if [[ -z "${build_version}" ]]; then
    build_version="$(git rev-list --count "${commit}")"
fi
if [[ ! "${build_version}" =~ ^[0-9]+(\.[0-9]+)*$ ]]; then
    echo "The build version must contain only dot-separated unsigned integers." >&2
    exit 64
fi

git_dir="$(git rev-parse --absolute-git-dir)"
receipt_directory="${git_dir}/ghostshell-release-rehearsals"
receipt_path="${receipt_directory}/${tag}.receipt"
if [[ "${reuse_pass}" == true && -f "${receipt_path}" ]]; then
    expected_receipt="status=pass
tag=${tag}
commit=${commit}
tree=${tree}"
    if [[ "$(sed -n '1,4p' "${receipt_path}")" == "${expected_receipt}" ]]; then
        echo "Reusing the passing local release rehearsal for ${tag} at ${commit}."
        exit 0
    fi
fi

xcode_application="${GHOSTSHELL_XCODE_APP:-/Applications/Xcode.app}"
if [[ ! -d "${xcode_application}/Contents/Developer" ]] \
    || ! DEVELOPER_DIR="${xcode_application}/Contents/Developer" \
        xcrun actool --version --output-format=human-readable-text 2>/dev/null \
        | grep -Eq 'short-bundle-version: (2[6-9]|[3-9][0-9]|[1-9][0-9]{2,})(\.|$)'; then
    xcode_application="$(find /Applications -maxdepth 1 -type d \
        -name 'Xcode_26*.app' -print 2>/dev/null | LC_ALL=C sort | tail -n 1)"
fi
if [[ -z "${xcode_application}" || ! -d "${xcode_application}/Contents/Developer" ]]; then
    echo "Local release rehearsal requires full Xcode 26 or newer." >&2
    exit 1
fi
export DEVELOPER_DIR="${xcode_application}/Contents/Developer"
actool_version="$(xcrun actool --version --output-format=human-readable-text)"
if ! grep -Eq 'short-bundle-version: (2[6-9]|[3-9][0-9]|[1-9][0-9]{2,})(\.|$)' \
    <<< "${actool_version}"; then
    echo "The selected Xcode does not provide actool 26 or newer." >&2
    exit 1
fi

if [[ -z "${GRAALVM_HOME:-}" || ! -x "${GRAALVM_HOME}/bin/native-image" ]]; then
    echo "GRAALVM_HOME must identify GraalVM 25.0.4 with native-image." >&2
    exit 1
fi
native_image_version="$("${GRAALVM_HOME}/bin/native-image" --version | head -n 1)"
if [[ ! "${native_image_version}" =~ ^native-image[[:space:]]+25\.0\.4([[:space:]]|$) ]]; then
    echo "Release rehearsal requires native-image 25.0.4; found ${native_image_version}." >&2
    exit 1
fi
export JAVA_HOME="${GRAALVM_HOME}"

native_aot_linker="${GHOSTSHELL_NATIVE_AOT_LINKER:-}"
if [[ -z "${native_aot_linker}" ]] && command -v brew >/dev/null 2>&1; then
    native_aot_linker="$(brew --prefix lld@22 2>/dev/null || true)/bin/ld64.lld"
fi
if [[ -z "${native_aot_linker}" || ! -x "${native_aot_linker}" ]]; then
    echo "Release rehearsal requires LLVM ld64.lld 22.x." >&2
    exit 1
fi
linker_version="$("${native_aot_linker}" --version)"
if [[ ! "${linker_version}" =~ LLD[[:space:]]22\. ]]; then
    echo "Release rehearsal requires LLVM lld 22.x; found ${linker_version}." >&2
    exit 1
fi
export GHOSTSHELL_NATIVE_AOT_LINKER="${native_aot_linker}"

for required_variable in \
    APPLE_CERTIFICATE_P12_BASE64 \
    APPLE_CERTIFICATE_PASSWORD \
    APPLE_DEVELOPER_ID_APPLICATION \
    APPLE_NOTARY_ISSUER_ID \
    APPLE_NOTARY_KEY_ID \
    APPLE_NOTARY_PRIVATE_KEY_BASE64; do
    if [[ -z "${!required_variable:-}" ]]; then
        echo "Required release environment variable ${required_variable} is unavailable." >&2
        exit 1
    fi
done
for required_command in assetutil cmake curl ditto file go mvn python3 security shasum spctl; do
    if ! command -v "${required_command}" >/dev/null 2>&1; then
        echo "Release rehearsal requires ${required_command}." >&2
        exit 1
    fi
done

working_directory="$(mktemp -d "${TMPDIR:-/tmp}/ghostshell-release-rehearsal.XXXXXX")"
working_directory="$(cd -- "${working_directory}" && pwd -P)"
dotnet_cli_home="${working_directory}/dotnet-home"
sealed_source="${working_directory}/sealed-source"
dependency_source="${working_directory}/dependency-source"
campaign_tool="${working_directory}/campaign-tool"
campaign_build="${working_directory}/campaign-build"
source_seal="${working_directory}/source-seal"
release_artifacts="${working_directory}/release-artifacts"
build_artifacts="${working_directory}/release-build"
nuget_packages="${working_directory}/nuget"
locked_maven_repository="${working_directory}/locked-maven"
signing_directory="${working_directory}/signing"
signing_keychain="${signing_directory}/release.keychain-db"
signing_password="$(uuidgen)"
signing_identity="${APPLE_DEVELOPER_ID_APPLICATION}"
notary_profile="ghostshell-local-$$"
previous_keychains=()
while IFS= read -r keychain; do
    keychain="$(printf '%s\n' "${keychain}" \
        | sed 's/^[[:space:]]*"//; s/"[[:space:]]*$//')"
    previous_keychains+=("${keychain}")
done < <(security list-keychains -d user)

cleanup() {
    if [[ ${#previous_keychains[@]} -gt 0 ]]; then
        security list-keychains -d user -s \
            ${previous_keychains[@]+"${previous_keychains[@]}"} >/dev/null 2>&1 || true
    fi
    if [[ -f "${signing_keychain}" ]]; then
        security delete-keychain "${signing_keychain}" >/dev/null 2>&1 || true
    fi
    chmod -R u+w "${working_directory}" 2>/dev/null || true
    rm -rf -- "${working_directory}"
}
trap cleanup EXIT

mkdir -p "${dotnet_cli_home}" "${signing_directory}"
export DOTNET_CLI_HOME="${dotnet_cli_home}"
prepare_signing_keychain() {
    printf '%s' "${APPLE_CERTIFICATE_P12_BASE64}" \
        | /usr/bin/base64 -D > "${signing_directory}/certificate.p12"
    printf '%s' "${APPLE_NOTARY_PRIVATE_KEY_BASE64}" \
        | /usr/bin/base64 -D > "${signing_directory}/AuthKey.p8"
    security create-keychain -p "${signing_password}" "${signing_keychain}"
    # Expire an unattended unlocked keychain, but do not lock on sleep: the build
    # can sleep before signing and explicitly unlocks again at that boundary.
    security set-keychain-settings -u -t 21600 "${signing_keychain}"
    security unlock-keychain -p "${signing_password}" "${signing_keychain}"
    security import "${signing_directory}/certificate.p12" \
        -k "${signing_keychain}" \
        -P "${APPLE_CERTIFICATE_PASSWORD}" \
        -T /usr/bin/codesign
    security set-key-partition-list \
        -S apple-tool:,apple: \
        -s \
        -k "${signing_password}" \
        "${signing_keychain}"
    security list-keychains -d user -s \
        "${signing_keychain}" \
        ${previous_keychains[@]+"${previous_keychains[@]}"}
    # Signing and notarization receive this keychain explicitly. Never make it the
    # user's default: unrelated apps could store new keys here and lose them when
    # this disposable keychain is deleted at the end of the rehearsal.
    security find-identity -v -p codesigning "${signing_keychain}" \
        | grep -Fq "${signing_identity}"
    xcrun notarytool store-credentials "${notary_profile}" \
        --keychain "${signing_keychain}" \
        --key "${signing_directory}/AuthKey.p8" \
        --key-id "${APPLE_NOTARY_KEY_ID}" \
        --issuer "${APPLE_NOTARY_ISSUER_ID}"
}

mkdir -p \
    "${sealed_source}" \
    "${dependency_source}" \
    "${campaign_tool}" \
    "${release_artifacts}/campaign-test-results" \
    "${build_artifacts}" \
    "${nuget_packages}"

echo "Running the complete repository gate before release assembly."
GHOSTSHELL_TEST_RESULTS_ROOT="${release_artifacts}/campaign-test-results" \
    ./scripts/check.sh --full

git archive --format=tar "${commit}" | tar -xf - -C "${dependency_source}"
NUGET_PACKAGES="${nuget_packages}" "${dotnet}" restore \
    "${dependency_source}/GhostShell.slnx" \
    --locked-mode
mkdir -p "${release_artifacts}/dependency-scan"
NUGET_PACKAGES="${nuget_packages}" "${dotnet}" package list \
    --project "${dependency_source}/GhostShell.slnx" \
    --vulnerable \
    --include-transitive \
    --format json \
    --output-version 1 \
    --no-restore > "${release_artifacts}/dependency-scan/nuget-audit.json"
"${dependency_source}/scripts/prepare-locked-maven-repository.py" \
    "${dependency_source}/native/sql-language-worker/maven-content-lock.json" \
    "${locked_maven_repository}"
(
    cd "${dependency_source}/native/sql-language-worker"
    mvn -B --offline \
        "-Dmaven.repo.local=${locked_maven_repository}" \
        clean \
        org.apache.maven.plugins:maven-dependency-plugin:3.8.1:list \
        -DincludeScope=runtime \
        -DoutputFile=target/runtime-dependencies.raw.txt \
        -DappendOutput=false \
        org.apache.maven.plugins:maven-dependency-plugin:3.8.1:copy-dependencies \
        -DincludeScope=runtime \
        -DoutputDirectory=target/runtime-jars \
        -DoverWriteReleases=true \
        -DoverWriteSnapshots=true \
        -DoverWriteIfNewer=true
)

grype_directory="${repository_dir}/.deps/tools/grype-0.117.0-darwin-arm64"
grype_archive="${grype_directory}/grype_0.117.0_darwin_arm64.tar.gz"
grype_execution_directory="$(mktemp -d "${working_directory}/grype.XXXXXX")"
grype_executable="${grype_execution_directory}/grype"
mkdir -p "${grype_directory}"
if [[ ! -f "${grype_archive}" ]]; then
    curl --fail --location --silent --show-error \
        https://github.com/anchore/grype/releases/download/v0.117.0/grype_0.117.0_darwin_arm64.tar.gz \
        --output "${grype_archive}"
fi
echo "bfcefa3f3b1690d9c77d847841b32ebd6106ab0e0e32f810924707e704d53584  ${grype_archive}" \
    | shasum -a 256 -c -
# Only the pinned archive is cached. Never execute a previously extracted
# cache entry: its content is not covered by the archive checksum above.
tar -xzf "${grype_archive}" -C "${grype_execution_directory}" grype
GRYPE_DB_CACHE_DIR="${repository_dir}/.deps/tools/grype-cache" \
    "${grype_executable}" \
    "dir:${dependency_source}/native/sql-language-worker/target/runtime-jars" \
    --output json \
    --file "${release_artifacts}/dependency-scan/maven-audit.json" \
    --fail-on low

git archive --format=tar "${commit}" | tar -xf - -C "${sealed_source}"
NUGET_PACKAGES="${nuget_packages}" "${dotnet}" publish \
    "${sealed_source}/tools/GhostShell.SecurityCampaign/GhostShell.SecurityCampaign.csproj" \
    --configuration Release \
    --artifacts-path "${campaign_build}" \
    --output "${campaign_tool}"
campaign_dll="${campaign_tool}/GhostShell.SecurityCampaign.dll"
"${dotnet}" "${campaign_dll}" \
    assemble-dependency-evidence \
    --source-commit "${commit}" \
    --nuget "${release_artifacts}/dependency-scan/nuget-audit.json" \
    --maven "${release_artifacts}/dependency-scan/maven-audit.json" \
    --output "${release_artifacts}/dependency-security-evidence"
"${dotnet}" "${campaign_dll}" \
    seal-release-source \
    --repository "${repository_dir}" \
    --source-root "${sealed_source}" \
    --source-commit "${commit}" \
    --source-tree "${tree}" \
    --tag "${tag}" \
    --output "${source_seal}"
mkdir -p "${sealed_source}/.deps" "${sealed_source}/native/artifacts"
chmod -R a-w "${sealed_source}"
chmod -R u+rwX "${sealed_source}/.deps" "${sealed_source}/native/artifacts"

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export GHOSTSHELL_RELEASE_SOURCE_ROOT="${sealed_source}"
export GHOSTSHELL_RELEASE_SOURCE_SEAL="${source_seal}"
export GHOSTSHELL_SECURITY_CAMPAIGN_TOOL="${campaign_dll}"
export GHOSTSHELL_RELEASE_ARTIFACTS="${release_artifacts}"
export GHOSTSHELL_BUILD_ARTIFACTS_ROOT="${build_artifacts}"
export GHOSTSHELL_DOTNET="${dotnet}"
export GHOSTSHELL_RELEASE_SOURCE_COMMIT="${commit}"
export GHOSTSHELL_RELEASE_SOURCE_TREE="${tree}"
export GHOSTSHELL_RELEASE_SOURCE_TAG="${tag}"
export NUGET_PACKAGES="${nuget_packages}"

version="${tag#v}"
cd "${sealed_source}"
"${dotnet}" run \
    --project tools/GhostShell.Packaging/GhostShell.Packaging.csproj \
    --configuration Release \
    --artifacts-path "${build_artifacts}/legal" \
    -- \
    macos-release-legal \
    --record licenses/macos-release-legal.json \
    --source-root . \
    --require-clearance
./scripts/build-libghostty-vt.sh --rid osx-arm64
./scripts/build-workspace-network-gateway.sh --rid osx-arm64
./scripts/build-workspace-runtime.sh
./scripts/build-openvpn-engine.sh
./scripts/build-macos-connection-engines.sh
./scripts/build-sql-language-worker.sh --local --rid osx-arm64
./scripts/build-cef-runtime.sh --rid osx-arm64 --dotnet "${dotnet}"
prepare_signing_keychain
security unlock-keychain -p "${signing_password}" "${signing_keychain}"
security find-identity -v -p codesigning "${signing_keychain}" \
    | grep -Fq "${signing_identity}"
./scripts/package-macos-github-release.sh \
    --version "${version}" \
    --build-version "${build_version}" \
    --sign-identity "${signing_identity}" \
    --notary-profile "${notary_profile}" \
    --keychain "${signing_keychain}" \
    --release-evidence-dir "${release_artifacts}/release-evidence" \
    --source-seal "${source_seal}" \
    --security-campaign-tool "${campaign_dll}" \
    --build-artifacts-root "${build_artifacts}/package" \
    --output-dir "${release_artifacts}/distribution"

archive="${release_artifacts}/distribution/GhostShell-macOS-arm64.zip"
verification_directory="${working_directory}/archive"
mkdir "${verification_directory}"
ditto -x -k "${archive}" "${verification_directory}"
extracted_app="${verification_directory}/GhostShell.app"
codesign --verify --deep --strict --verbose=2 "${extracted_app}"
xcrun stapler validate "${extracted_app}"
spctl --assess --type execute --verbose=2 "${extracted_app}"
info_plist="${extracted_app}/Contents/Info.plist"
test "$(plutil -extract CFBundleShortVersionString raw "${info_plist}")" = "${version}"
test "$(plutil -extract CFBundleDisplayName raw "${info_plist}")" = "GhostSHELL"
test "$(plutil -extract CFBundleExecutable raw "${info_plist}")" = "GhostShell"
test "$(plutil -extract CFBundleIdentifier raw "${info_plist}")" = "app.ghostshell"
test "$(plutil -extract CFBundleIconName raw "${info_plist}")" = "GhostShell"
assets_car="${extracted_app}/Contents/Resources/Assets.car"
assetutil --info "${assets_car}" > "${verification_directory}/Assets.info.json"
grep -Fq '"AssetType" : "Icon Image"' "${verification_directory}/Assets.info.json"
grep -Fq '"Name" : "GhostShell"' "${verification_directory}/Assets.info.json"
test -x "${extracted_app}/Contents/MacOS/UpdateMac"
test "$(readlink "${extracted_app}/Contents/MacOS/sq.version")" \
    = "../Resources/sq.version"

evidence_arguments=(
    --repository "${sealed_source}"
    --source-commit "${commit}"
    --source-tree "${tree}"
    --tag "${tag}"
    --run-id 0
    --run-attempt 1
    --source-seal "${source_seal}"
    --build-identity "${release_artifacts}/release-evidence/release-build-identity.json"
    --archive "${archive}"
    --package "${extracted_app}"
    --test-results "${release_artifacts}/campaign-test-results"
    --dependency-evidence "${release_artifacts}/dependency-security-evidence/evidence.json"
    --notarization-evidence "${release_artifacts}/release-evidence/notarization.json"
)
"${dotnet}" "${campaign_dll}" \
    validate-local-release-inputs \
    "${evidence_arguments[@]}"

mkdir -p "${receipt_directory}"
archive_sha256="$(shasum -a 256 "${archive}" | awk '{print $1}')"
xcode_version="$(xcodebuild -version | tr '\n' ' ')"
{
    echo "status=pass"
    echo "tag=${tag}"
    echo "commit=${commit}"
    echo "tree=${tree}"
    echo "archiveSha256=${archive_sha256}"
    echo "buildVersion=${build_version}"
    echo "xcode=${xcode_version}"
    echo "nativeImage=${native_image_version}"
    echo "signingIdentity=${signing_identity}"
} > "${receipt_path}"
chmod 600 "${receipt_path}"
echo "Local signed and notarized release rehearsal passed for ${tag} at ${commit}."
