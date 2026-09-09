#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"

if [[ -n "${ASURA_DOTNET:-}" ]]; then
    dotnet="${ASURA_DOTNET}"
elif [[ -x "${repository_dir}/.dotnet/dotnet" ]]; then
    dotnet="${repository_dir}/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
    dotnet="$(command -v dotnet)"
else
    echo "The pinned .NET SDK is unavailable." >&2
    exit 1
fi

failure=0
while IFS= read -r project; do
    lock_file="$(dirname "${project}")/packages.lock.json"
    if [[ ! -f "${lock_file}" ]]; then
        echo "Missing lock file for ${project#${repository_dir}/}." >&2
        failure=1
    fi
done < <(
    find \
        "${repository_dir}/src" \
        "${repository_dir}/tests" \
        "${repository_dir}/tools" \
        "${repository_dir}/scripts/acceptance" \
        -name '*.csproj' \
        -print |
        sort
)

while IFS= read -r project; do
    for runtime_identifier in linux-x64 linux-arm64; do
        lock_file="$(dirname "${project}")/packages.${runtime_identifier}.lock.json"
        if [[ ! -f "${lock_file}" ]]; then
            echo "Missing ${runtime_identifier} release lock file for ${project#${repository_dir}/}." >&2
            failure=1
        fi
    done
done < <(find "${repository_dir}/src" -name '*.csproj' -print | sort)

for project in \
    "${repository_dir}/src/Asura.Desktop/Asura.Desktop.csproj" \
    "${repository_dir}/tests/Asura.Architecture.Tests/Asura.Architecture.Tests.csproj" \
    "${repository_dir}/tools/Asura.SingleInstanceTestHost/Asura.SingleInstanceTestHost.csproj"
do
    lock_file="$(dirname "${project}")/packages.windows.lock.json"
    if [[ ! -f "${lock_file}" ]]; then
        echo "Missing Windows managed-build lock file for ${project#${repository_dir}/}." >&2
        failure=1
    fi
done

inline_versions="$(
    find \
        "${repository_dir}/src" \
        "${repository_dir}/tests" \
        "${repository_dir}/tools" \
        "${repository_dir}/scripts/acceptance" \
        -name '*.csproj' \
        -exec grep -HnE '<PackageReference[^>]+Version=' {} + ||
        true
)"
if [[ -n "${inline_versions}" ]]; then
    echo "Package versions must be declared in Directory.Packages.props:" >&2
    echo "${inline_versions}" >&2
    failure=1
fi

unpinned_actions="$(
    grep -R -nE 'uses:[[:space:]]+[^[:space:]]+@' "${repository_dir}/.github/workflows" |
        grep -vE 'uses:[[:space:]]+\./' |
        grep -vE '@[0-9a-fA-F]{40}([[:space:]]|$)' ||
        true
)"
if [[ -n "${unpinned_actions}" ]]; then
    echo "GitHub Actions must be pinned to full commit SHAs:" >&2
    echo "${unpinned_actions}" >&2
    failure=1
fi

if [[ "${failure}" != "0" ]]; then
    exit "${failure}"
fi

audit_result="$(mktemp -t asura-nuget-audit.XXXXXX)"
trap 'rm -f "${audit_result}"' EXIT

"${dotnet}" package list \
    --project "${repository_dir}/Asura.slnx" \
    --vulnerable \
    --include-transitive \
    --format json \
    --output-version 1 \
    --no-restore > "${audit_result}"

if grep -q '"vulnerabilities"[[:space:]]*:' "${audit_result}"; then
    echo "NuGet reported vulnerable direct or transitive dependencies:" >&2
    cat "${audit_result}" >&2
    exit 1
fi

# The solution restore validates the ordinary graph only. Release packaging
# selects separate reviewed lock files, so validate each graph before a tag is
# allowed to discover stale project or package dependencies.
"${dotnet}" restore "${repository_dir}/Asura.slnx" \
    -p:AsuraWindowsBuild=true \
    --locked-mode \
    --verbosity quiet

desktop_project="${repository_dir}/src/Asura.Desktop/Asura.Desktop.csproj"
runtime_identifiers=(linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64)
for runtime_identifier in "${runtime_identifiers[@]}"; do
    "${dotnet}" restore "${desktop_project}" \
        --runtime "${runtime_identifier}" \
        --locked-mode \
        --verbosity quiet
done
"${dotnet}" restore "${desktop_project}" \
    --runtime osx-arm64 \
    --locked-mode \
    --verbosity quiet \
    -p:AsuraMacReleaseNativeAot=true
"${dotnet}" restore "${repository_dir}/Asura.slnx" \
    --locked-mode \
    --verbosity quiet

echo "Dependency audit passed: lock files, central versions, and NuGet advisories are clean."
