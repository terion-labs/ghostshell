#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
section="${1:-}"
restore_mode="${2:-}"

usage() {
    cat >&2 <<'EOF'
Usage: ./scripts/test-section.sh <section|all|validate> [--no-restore]

Sections: core, agent, app, services, data-browser, terminal-host
EOF
}

if [[ -z "${section}" || $# -gt 2 ]]; then
    usage
    exit 64
fi
if [[ -n "${restore_mode}" && "${restore_mode}" != "--no-restore" ]]; then
    usage
    exit 64
fi

core_projects=(
    "tests/GhostShell.Application.Tests/GhostShell.Application.Tests.csproj"
    "tests/GhostShell.Architecture.Tests/GhostShell.Architecture.Tests.csproj"
    "tests/GhostShell.Core.Tests/GhostShell.Core.Tests.csproj"
    "tests/GhostShell.Protocol.Tests/GhostShell.Protocol.Tests.csproj"
)
agent_projects=(
    "tests/GhostShell.Agent.Providers.Tests/GhostShell.Agent.Providers.Tests.csproj"
    "tests/GhostShell.Agent.Runtime.Tests/GhostShell.Agent.Runtime.Tests.csproj"
    "tests/GhostShell.Agent.Tests/GhostShell.Agent.Tests.csproj"
    "tests/GhostShell.Mcp.Tests/GhostShell.Mcp.Tests.csproj"
)
app_projects=(
    "tests/GhostShell.App.Tests/GhostShell.App.Tests.csproj"
)
services_projects=(
    "tests/GhostShell.Docker.Tests/GhostShell.Docker.Tests.csproj"
    "tests/GhostShell.Files.Tests/GhostShell.Files.Tests.csproj"
    "tests/GhostShell.Git.Tests/GhostShell.Git.Tests.csproj"
    "tests/GhostShell.Infrastructure.Tests/GhostShell.Infrastructure.Tests.csproj"
    "tests/GhostShell.Monitoring.Tests/GhostShell.Monitoring.Tests.csproj"
    "tests/GhostShell.Previews.Tests/GhostShell.Previews.Tests.csproj"
    "tests/GhostShell.SshNet.Tests/GhostShell.SshNet.Tests.csproj"
    "tests/GhostShell.Updates.Tests/GhostShell.Updates.Tests.csproj"
)
data_browser_projects=(
    "tests/GhostShell.AccessibilityAcceptance.Tests/GhostShell.AccessibilityAcceptance.Tests.csproj"
    "tests/GhostShell.Browser.Tests/GhostShell.Browser.Tests.csproj"
    "tests/GhostShell.Databases.IntegrationTests/GhostShell.Databases.IntegrationTests.csproj"
    "tests/GhostShell.Databases.Tests/GhostShell.Databases.Tests.csproj"
    "tests/GhostShell.Redis.Tests/GhostShell.Redis.Tests.csproj"
    "tests/GhostShell.SoakAcceptance.Tests/GhostShell.SoakAcceptance.Tests.csproj"
)
terminal_host_projects=(
    "tests/GhostShell.SessionHost.Tests/GhostShell.SessionHost.Tests.csproj"
    "tests/GhostShell.Terminal.Tests/GhostShell.Terminal.Tests.csproj"
    "tests/GhostShell.TerminalAcceptance.Tests/GhostShell.TerminalAcceptance.Tests.csproj"
)
all_projects=(
    "${core_projects[@]}"
    "${agent_projects[@]}"
    "${app_projects[@]}"
    "${services_projects[@]}"
    "${data_browser_projects[@]}"
    "${terminal_host_projects[@]}"
)

# Every test project must belong to exactly one section. This turns an added but
# unassigned test project into an immediate CI failure instead of a coverage gap.
actual_projects="$(
    cd "${repository_dir}"
    find tests -mindepth 2 -maxdepth 2 -name '*.csproj' -print | LC_ALL=C sort
)"
configured_projects="$(printf '%s\n' "${all_projects[@]}" | LC_ALL=C sort)"
duplicate_projects="$(printf '%s\n' "${all_projects[@]}" | LC_ALL=C sort | uniq -d)"
if [[ -n "${duplicate_projects}" || "${configured_projects}" != "${actual_projects}" ]]; then
    echo "Test sections must contain every test project exactly once." >&2
    if [[ -n "${duplicate_projects}" ]]; then
        echo "Duplicate assignments:" >&2
        printf '%s\n' "${duplicate_projects}" >&2
    fi
    diff -u \
        <(printf '%s\n' "${actual_projects}") \
        <(printf '%s\n' "${configured_projects}") >&2 || true
    exit 1
fi

if [[ "${section}" == "validate" ]]; then
    exit 0
fi

case "${section}" in
    core) projects=("${core_projects[@]}") ;;
    agent) projects=("${agent_projects[@]}") ;;
    app) projects=("${app_projects[@]}") ;;
    services) projects=("${services_projects[@]}") ;;
    data-browser) projects=("${data_browser_projects[@]}") ;;
    terminal-host) projects=("${terminal_host_projects[@]}") ;;
    all) projects=("${all_projects[@]}") ;;
    *)
        usage
        exit 64
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
export NUGET_PACKAGES="${NUGET_PACKAGES:-${repository_dir}/.nuget/packages}"

cd "${repository_dir}"
for project in "${projects[@]}"; do
    project_name="$(basename "${project}" .csproj)"
    results_directory="${repository_dir}/.test-results/${section}/${project_name}"
    test_command=("${dotnet}" test "${project}"
        --configuration Release
        --results-directory "${results_directory}"
        --logger "trx;LogFileName=${project_name}.trx")

    if [[ "${restore_mode}" == "--no-restore" ]]; then
        test_command+=(--no-restore)
    fi
    if [[ "${GHOSTSHELL_COLLECT_COVERAGE:-0}" == "1" ]] &&
       grep -q '<PackageReference Include="coverlet.collector"' "${project}"; then
        test_command+=(--collect "XPlat Code Coverage")
    fi

    "${test_command[@]}"
done
