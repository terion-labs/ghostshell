#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_dir="$(cd "${script_dir}/.." && pwd)"
dotnet="${repository_dir}/.dotnet/dotnet"
project="${repository_dir}/tests/Asura.Databases.IntegrationTests/Asura.Databases.IntegrationTests.csproj"

if [[ ! -x "${dotnet}" ]]; then
    echo "Run ASURA_SKIP_NATIVE=1 ./scripts/bootstrap.sh first." >&2
    exit 1
fi

if [[ "${1-}" == "--help" || "${1-}" == "-h" ]]; then
    cat <<'USAGE'
Usage: ./scripts/test-database-viewer-integration.sh [provider,...|all] [dotnet test arguments]

Examples:
  ./scripts/test-database-viewer-integration.sh
  ./scripts/test-database-viewer-integration.sh sqlite,duckdb
  ./scripts/test-database-viewer-integration.sh redis
  ./scripts/test-database-viewer-integration.sh postgres --logger "console;verbosity=detailed"
  ASURA_RUN_SQL_LANGUAGE_NATIVE=1 \
    ASURA_SQL_LANGUAGE_WORKER="$PWD/native/artifacts/osx-arm64/asura-sql-language" \
    ./scripts/test-database-viewer-integration.sh sqlite

If no provider argument is supplied, the script uses
ASURA_DATABASE_INTEGRATION_PROVIDERS, then falls back to all.

Set ASURA_RUN_SQL_LANGUAGE_NATIVE=1 to make the real Calcite worker a
required part of the database and rendered-editor journeys. The worker path
must name an executable built for the host operating system and architecture.
USAGE
    exit 0
fi

native_required="${ASURA_RUN_SQL_LANGUAGE_NATIVE:-0}"
case "${native_required}" in
    0)
        ;;
    1)
        worker_path="${ASURA_SQL_LANGUAGE_WORKER:-}"
        if [[ -z "${worker_path}" ]]; then
            echo "ASURA_SQL_LANGUAGE_WORKER is required when ASURA_RUN_SQL_LANGUAGE_NATIVE=1." >&2
            echo "Build the host worker with ./scripts/build-sql-language-worker.sh, then pass its artifact path." >&2
            exit 1
        fi

        if [[ "${worker_path}" != /* ]]; then
            worker_path="${repository_dir}/${worker_path}"
        fi

        if [[ ! -f "${worker_path}" ]]; then
            echo "The required SQL language worker does not exist: ${worker_path}" >&2
            exit 1
        fi

        if [[ ! -x "${worker_path}" ]]; then
            echo "The required SQL language worker is not executable: ${worker_path}" >&2
            exit 1
        fi

        worker_directory="$(cd "$(dirname "${worker_path}")" && pwd -P)"
        export ASURA_RUN_SQL_LANGUAGE_NATIVE=1
        export ASURA_SQL_LANGUAGE_WORKER="${worker_directory}/$(basename "${worker_path}")"
        ;;
    *)
        echo "ASURA_RUN_SQL_LANGUAGE_NATIVE must be 0 or 1; received: ${native_required}" >&2
        exit 2
        ;;
esac

providers="${ASURA_DATABASE_INTEGRATION_PROVIDERS:-all}"
if [[ $# -gt 0 && "${1}" != -* ]]; then
    providers="${1}"
    shift
fi

# Provider IDs are case-insensitive at this command boundary. Remove whitespace
# so the value passed to the test process has the canonical comma-list shape.
providers="$(printf '%s' "${providers}" | tr '[:upper:]' '[:lower:]' | tr -d '[:space:]')"
if [[ -z "${providers}" ]]; then
    echo "The provider list cannot be empty." >&2
    exit 2
fi

docker_required=0
if [[ "${providers}" == "all" ]]; then
    docker_required=1
else
    IFS=',' read -r -a selected_providers <<< "${providers}"
    for provider in "${selected_providers[@]}"; do
        case "${provider}" in
            sqlite|duckdb)
                ;;
            postgres|cockroach|redshift|mysql|mariadb|sqlserver|oracle|firebird|clickhouse|redis)
                docker_required=1
                ;;
            *)
                echo "Unknown database integration provider: ${provider}" >&2
                echo "Known providers: sqlite, duckdb, postgres, cockroach, redshift, mysql, mariadb, sqlserver, oracle, firebird, clickhouse, redis" >&2
                exit 2
                ;;
        esac
    done
fi

if [[ "${docker_required}" == "1" ]]; then
    if ! command -v docker >/dev/null 2>&1; then
        echo "Docker is required for the selected database provider(s)." >&2
        exit 1
    fi

    if ! docker info >/dev/null 2>&1; then
        echo "Docker is installed, but its daemon is not available." >&2
        exit 1
    fi
fi

export ASURA_RUN_DATABASE_INTEGRATION=1
export ASURA_DATABASE_INTEGRATION_PROVIDERS="${providers}"

test_arguments=("$@")
if [[ "${providers}" == "redis" ]]; then
    has_filter=0
    for argument in "${test_arguments[@]}"; do
        case "${argument}" in
            --filter|--filter=*)
                has_filter=1
                ;;
        esac
    done

    # The relational conformance theory intentionally has no rows when Redis is
    # the sole selection; xUnit v2 treats that as an error unless it is filtered.
    if [[ "${has_filter}" == "0" ]]; then
        test_arguments+=(--filter "FullyQualifiedName~RedisDatabasePanelIntegrationTests")
    fi
fi

exec "${dotnet}" test "${project}" --configuration Release "${test_arguments[@]}"
