#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
mode="${1:---full}"

case "${mode}" in
    --quick|--full)
        ;;
    *)
        echo "Usage: $0 [--quick|--full]" >&2
        exit 2
        ;;
esac

if [[ -n "${GHOSTSHELL_DOTNET:-}" ]]; then
    dotnet="${GHOSTSHELL_DOTNET}"
elif [[ -x "${repository_dir}/.dotnet/dotnet" ]]; then
    dotnet="${repository_dir}/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
    dotnet="$(command -v dotnet)"
else
    echo "The pinned .NET SDK is unavailable. Run ./scripts/bootstrap.sh first." >&2
    exit 1
fi

expected_sdk="$(
    sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
        "${repository_dir}/global.json" |
        head -n 1
)"
actual_sdk="$("${dotnet}" --version)"
if [[ -z "${expected_sdk}" || "${actual_sdk}" != "${expected_sdk}" ]]; then
    echo "Expected .NET SDK ${expected_sdk:-<unreadable>}, found ${actual_sdk}." >&2
    exit 1
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export GHOSTSHELL_DOTNET="${dotnet}"
export NUGET_PACKAGES="${repository_dir}/.nuget/packages"

cd "${repository_dir}"

"${script_dir}/check-network-native.sh" "${mode}"
"${dotnet}" tool restore
"${dotnet}" restore GhostShell.slnx --locked-mode
"${script_dir}/audit-dependencies.sh"
"${dotnet}" format GhostShell.slnx \
    --verify-no-changes \
    --no-restore \
    --exclude vendor/exclr8cef \
    --exclude vendor/sqlclient/tests/upstream \
    --severity warn
"${dotnet}" build GhostShell.slnx \
    --configuration Release \
    --no-restore \
    --nologo
"${script_dir}/test-section.sh" validate

run_test_project() {
    local project="$1"
    local project_name
    local results_directory
    local results_root
    local -a test_command

    project_name="$(basename "${project}" .csproj)"
    results_root="${GHOSTSHELL_TEST_RESULTS_ROOT:-${repository_dir}/.test-results}"
    results_directory="${results_root}/${project_name}"

    test_command=("${dotnet}" test "${project}" \
        --configuration Release \
        --no-build \
        --no-restore \
        --results-directory "${results_directory}" \
        --logger "trx;LogFileName=${project_name}.trx")

    if [[ "${GHOSTSHELL_COLLECT_COVERAGE:-0}" == "1" ]] &&
       grep -q '<PackageReference Include="coverlet.collector"' "${project}"; then
        test_command+=(--collect "XPlat Code Coverage")
    fi

    "${test_command[@]}"
}

if [[ "${mode}" == "--quick" ]]; then
    run_test_project \
        "${repository_dir}/tests/GhostShell.Architecture.Tests/GhostShell.Architecture.Tests.csproj"
else
    while IFS= read -r project; do
        run_test_project "${project}"
    done < <(find "${repository_dir}/tests" -mindepth 2 -maxdepth 2 -name '*.csproj' -print | sort)
fi
